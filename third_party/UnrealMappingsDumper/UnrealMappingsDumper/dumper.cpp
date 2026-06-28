#include "pch.h"

#include "dumper.h"
#include "app.h"
#include "writer.h"
#include "oodle.h"

namespace
{
	constexpr uint32_t kMaxPropertyWalk = 65535;
	constexpr uint32_t kObjectScanProgressInterval = 4096;
	constexpr uint32_t kObjectScanAnomalyLogLimit = 128;
	constexpr int kMaxFNameChars = 1024;
	constexpr uint8_t kUsmapVersionLargeEnums = 3;
	constexpr int kChildPropertyOffsetMin = 0x40;
	constexpr int kChildPropertyOffsetMax = 0x98;
	constexpr int kChildPropertyOffsetStep = 0x8;
	constexpr int kFieldNextOffsetMin = 0x10;
	constexpr int kFieldNextOffsetMax = 0x58;
	constexpr int kFieldNextOffsetStep = 0x8;
	constexpr int kFieldClassOffsetMin = 0x0;
	constexpr int kFieldClassOffsetMax = 0x10;
	constexpr int kFieldClassOffsetStep = 0x8;
	constexpr uint32_t kPropertyScanStructLimit = 4096;
	constexpr uint32_t kPropertyScanChainLimit = 32;

	struct ChildPropertyOffsetScore
	{
		int Offset = 0;
		uint32_t NonNullHeads = 0;
		uint32_t ReadableHeads = 0;
		uint32_t PropertyClassHeads = 0;
		uint32_t ChainNodes = 0;
		uint32_t ChainMax = 0;
		uint32_t Faults = 0;
	};

	struct FieldNextOffsetScore
	{
		int ChildOffset = 0;
		int NextOffset = 0;
		uint32_t Heads = 0;
		uint32_t ChainNodes = 0;
		uint32_t ChainMax = 0;
		uint32_t Faults = 0;
	};

	struct FieldLayoutScore
	{
		int ChildOffset = 0;
		int ClassOffset = 0;
		int NextOffset = 0;
		uint32_t Heads = 0;
		uint32_t ChainNodes = 0;
		uint32_t ChainMax = 0;
		uint32_t Faults = 0;
	};

	static bool IsReadableMemory(const void* Address, size_t MinSize)
	{
		if (!Address)
			return false;

		MEMORY_BASIC_INFORMATION Info{};
		if (!VirtualQuery(Address, &Info, sizeof(Info)))
			return false;
		if (Info.State != MEM_COMMIT)
			return false;
		if (Info.Protect & PAGE_GUARD)
			return false;

		const auto Protect = Info.Protect & 0xff;
		if (Protect == PAGE_NOACCESS)
			return false;

		auto RegionStart = reinterpret_cast<uintptr_t>(Info.BaseAddress);
		auto RegionEnd = RegionStart + Info.RegionSize;
		auto Pointer = reinterpret_cast<uintptr_t>(Address);
		return Pointer >= RegionStart && Pointer + MinSize <= RegionEnd;
	}

	static bool TryGetObjectByIndex(int Index, UObject*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = ObjObjects::GetObjectByIndex(Index);
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadObjectClass(UObject* Object, UClass*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Object->Class();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadPointerAt(const void* Base, int Offset, void*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = *reinterpret_cast<void* const*>(reinterpret_cast<const uint8_t*>(Base) + Offset);
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadFieldClass(FProperty* Property, FFieldClass*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetClass();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadFieldClassId(FFieldClass* FieldClass, EClassCastFlags& Out, DWORD& Code)
	{
		Out = CASTCLASS_None;
		Code = 0;
		__try
		{
			Out = FieldClass->GetId();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadObjectFName(UObject* Object, FName& Out, DWORD& Code)
	{
		Out = FName(0);
		Code = 0;
		__try
		{
			Out = Object->GetFName();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadStructSuper(UStruct* Struct, UStruct*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Struct->Super();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadStructChildProperties(UStruct* Struct, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Struct->ChildProperties();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadPropertyFName(FProperty* Property, FName& Out, DWORD& Code)
	{
		Out = FName(0);
		Code = 0;
		__try
		{
			Out = Property->GetFName();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadPropertyNext(FProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = static_cast<FProperty*>(Property->GetNext());
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadBytePropertyEnum(FByteProperty* Property, UEnum*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetEnum();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadStructPropertyStruct(FStructProperty* Property, UScriptStruct*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetStruct();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadArrayPropertyInner(FArrayProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetInner();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadSetPropertyElement(FSetProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetElement();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadMapPropertyKey(FMapProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetKey();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadMapPropertyValue(FMapProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetValue();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadEnumPropertyUnderlying(FEnumProperty* Property, FProperty*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetUnderlying();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadEnumPropertyEnum(FEnumProperty* Property, UEnum*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = Property->GetEnum();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadEnumNames(UEnum* Enum, UEnum::EnumNameMap*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = &Enum->Names();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadEnumNameCount(UEnum::EnumNameMap* Names, int& Out, DWORD& Code)
	{
		Out = 0;
		Code = 0;
		__try
		{
			Out = Names->Num();
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryReadEnumNameKey(UEnum::EnumNameMap* Names, int Index, FName& Out, DWORD& Code)
	{
		Out = FName(0);
		Code = 0;
		__try
		{
			Out = (*Names)[Index].Key;
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool TryFNameToWide(FName Name, const wchar_t*& Out, int& Length, DWORD& Code)
	{
		Out = nullptr;
		Length = 0;
		Code = 0;
		FString Ret;

		__try
		{
			FNameToString(&Name, Ret);
			Out = Ret.Data();
			if (!Out)
				return true;

			while (Length < kMaxFNameChars && Out[Length])
				Length++;
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			Out = nullptr;
			Length = 0;
			return false;
		}
	}

	static bool TryFNameToBuffer(FName Name, wchar_t* Out, int Capacity, int& Length, DWORD& Code)
	{
		Length = 0;
		Code = 0;
		if (!Out || Capacity <= 0)
			return false;

		Out[0] = L'\0';
		FString Ret;

		__try
		{
			FNameToString(&Name, Ret);
			const auto Data = Ret.Data();
			if (!Data)
				return true;

			while (Length + 1 < Capacity && Length < kMaxFNameChars && Data[Length])
			{
				Out[Length] = Data[Length];
				Length++;
			}
			Out[Length] = L'\0';
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			Length = 0;
			Out[0] = L'\0';
			return false;
		}
	}

	static bool TryReadFieldClassName(FFieldClass* FieldClass, wchar_t* Out, int Capacity, int& Length, DWORD& Code)
	{
		Length = 0;
		Code = 0;
		if (!FieldClass || !IsReadableMemory(FieldClass, 0x10))
			return false;

		FName Name(0);
		__try
		{
			Name = FieldClass->GetFName();
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}

		return TryFNameToBuffer(Name, Out, Capacity, Length, Code);
	}

	static bool TryFindClassByName(const wchar_t* Name, UClass*& Out, DWORD& Code)
	{
		Out = nullptr;
		Code = 0;
		__try
		{
			Out = ObjObjects::FindObjectByName<UClass>(Name);
			return true;
		}
		__except (Code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER)
		{
			return false;
		}
	}

	static bool IsLikelyPropertyClassId(EClassCastFlags Id)
	{
		const auto Value = static_cast<uint64_t>(Id);
		constexpr uint64_t PropertyMask =
			static_cast<uint64_t>(CASTCLASS_FInt8Property) |
			static_cast<uint64_t>(CASTCLASS_FByteProperty) |
			static_cast<uint64_t>(CASTCLASS_FIntProperty) |
			static_cast<uint64_t>(CASTCLASS_FFloatProperty) |
			static_cast<uint64_t>(CASTCLASS_FUInt64Property) |
			static_cast<uint64_t>(CASTCLASS_FClassProperty) |
			static_cast<uint64_t>(CASTCLASS_FUInt32Property) |
			static_cast<uint64_t>(CASTCLASS_FInterfaceProperty) |
			static_cast<uint64_t>(CASTCLASS_FNameProperty) |
			static_cast<uint64_t>(CASTCLASS_FStrProperty) |
			static_cast<uint64_t>(CASTCLASS_FProperty) |
			static_cast<uint64_t>(CASTCLASS_FObjectProperty) |
			static_cast<uint64_t>(CASTCLASS_FBoolProperty) |
			static_cast<uint64_t>(CASTCLASS_FUInt16Property) |
			static_cast<uint64_t>(CASTCLASS_FStructProperty) |
			static_cast<uint64_t>(CASTCLASS_FArrayProperty) |
			static_cast<uint64_t>(CASTCLASS_FInt64Property) |
			static_cast<uint64_t>(CASTCLASS_FDelegateProperty) |
			static_cast<uint64_t>(CASTCLASS_FMulticastDelegateProperty) |
			static_cast<uint64_t>(CASTCLASS_FObjectPropertyBase) |
			static_cast<uint64_t>(CASTCLASS_FWeakObjectProperty) |
			static_cast<uint64_t>(CASTCLASS_FLazyObjectProperty) |
			static_cast<uint64_t>(CASTCLASS_FSoftObjectProperty) |
			static_cast<uint64_t>(CASTCLASS_FTextProperty) |
			static_cast<uint64_t>(CASTCLASS_FInt16Property) |
			static_cast<uint64_t>(CASTCLASS_FDoubleProperty) |
			static_cast<uint64_t>(CASTCLASS_FSoftClassProperty) |
			static_cast<uint64_t>(CASTCLASS_FMapProperty) |
			static_cast<uint64_t>(CASTCLASS_FSetProperty) |
			static_cast<uint64_t>(CASTCLASS_FEnumProperty) |
			static_cast<uint64_t>(CASTCLASS_FMulticastInlineDelegateProperty) |
			static_cast<uint64_t>(CASTCLASS_FMulticastSparseDelegateProperty) |
			static_cast<uint64_t>(CASTCLASS_FFieldPathProperty) |
			static_cast<uint64_t>(CASTCLASS_FObjectPtrProperty) |
			static_cast<uint64_t>(CASTCLASS_FClassPtrProperty);

		return Value != 0 && (Value & PropertyMask) != 0;
	}

	static uint32_t ScorePropertyChain(FProperty* Head, DWORD& Code)
	{
		uint32_t Depth = 0;
		auto Current = Head;

		while (Current && Depth < kPropertyScanChainLimit)
		{
			if (!IsReadableMemory(Current, 0x40))
				break;

			FFieldClass* FieldClass = nullptr;
			if (!TryReadFieldClass(Current, FieldClass, Code) || !FieldClass || !IsReadableMemory(FieldClass, 0x20))
				break;

			EClassCastFlags Id = CASTCLASS_None;
			if (!TryReadFieldClassId(FieldClass, Id, Code) || !IsLikelyPropertyClassId(Id))
				break;

			Depth++;

			FProperty* Next = nullptr;
			if (!TryReadPropertyNext(Current, Next, Code))
				break;
			Current = Next;
		}

		return Depth;
	}

	static uint32_t ScorePropertyChainWithNextOffset(FProperty* Head, int NextOffset, DWORD& Code)
	{
		uint32_t Depth = 0;
		auto Current = Head;

		while (Current && Depth < kPropertyScanChainLimit)
		{
			if (!IsReadableMemory(Current, 0x40))
				break;

			FFieldClass* FieldClass = nullptr;
			if (!TryReadFieldClass(Current, FieldClass, Code) || !FieldClass || !IsReadableMemory(FieldClass, 0x20))
				break;

			EClassCastFlags Id = CASTCLASS_None;
			if (!TryReadFieldClassId(FieldClass, Id, Code) || !IsLikelyPropertyClassId(Id))
				break;

			Depth++;

			void* NextRaw = nullptr;
			if (!TryReadPointerAt(Current, NextOffset, NextRaw, Code))
				break;

			Current = static_cast<FProperty*>(NextRaw);
		}

		return Depth;
	}

	static uint32_t ScorePropertyChainWithFieldOffsets(FProperty* Head, int ClassOffset, int NextOffset, DWORD& Code)
	{
		uint32_t Depth = 0;
		auto Current = Head;

		while (Current && Depth < kPropertyScanChainLimit)
		{
			if (!IsReadableMemory(Current, 0x40))
				break;

			void* FieldClassRaw = nullptr;
			if (!TryReadPointerAt(Current, ClassOffset, FieldClassRaw, Code) || !FieldClassRaw || !IsReadableMemory(FieldClassRaw, 0x20))
				break;

			EClassCastFlags Id = CASTCLASS_None;
			if (!TryReadFieldClassId(static_cast<FFieldClass*>(FieldClassRaw), Id, Code) || !IsLikelyPropertyClassId(Id))
				break;

			Depth++;

			void* NextRaw = nullptr;
			if (!TryReadPointerAt(Current, NextOffset, NextRaw, Code))
				break;

			Current = static_cast<FProperty*>(NextRaw);
		}

		return Depth;
	}

}

static EPropertyType GetPropertyType(FProperty* Prop)
{
	const auto FieldClass = Prop->GetClass();
	wchar_t ClassName[128]{};
	int ClassNameLength = 0;
	DWORD Code = 0;
	const auto HasClassName = TryReadFieldClassName(FieldClass, ClassName, static_cast<int>(std::size(ClassName)), ClassNameLength, Code);
	const auto IsClassName = [&](const wchar_t* Expected)
	{
		return HasClassName && std::wcscmp(ClassName, Expected) == 0;
	};

	if (IsClassName(L"ObjectProperty") ||
		IsClassName(L"ClassProperty") ||
		IsClassName(L"ObjectPtrProperty") ||
		IsClassName(L"ClassPtrProperty"))
		return EPropertyType::ObjectProperty;

	if (IsClassName(L"StructProperty"))
		return EPropertyType::StructProperty;

	if (IsClassName(L"Int8Property"))
		return EPropertyType::Int8Property;

	if (IsClassName(L"Int16Property"))
		return EPropertyType::Int16Property;

	if (IsClassName(L"IntProperty"))
		return EPropertyType::IntProperty;

	if (IsClassName(L"Int64Property"))
		return EPropertyType::Int64Property;

	if (IsClassName(L"UInt16Property"))
		return EPropertyType::UInt16Property;

	if (IsClassName(L"UInt32Property"))
		return EPropertyType::UInt32Property;

	if (IsClassName(L"UInt64Property"))
		return EPropertyType::UInt64Property;

	if (IsClassName(L"ArrayProperty"))
		return EPropertyType::ArrayProperty;

	if (IsClassName(L"FloatProperty"))
		return EPropertyType::FloatProperty;

	if (IsClassName(L"DoubleProperty") || IsClassName(L"LargeWorldCoordinatesRealProperty"))
		return EPropertyType::DoubleProperty;

	if (IsClassName(L"BoolProperty"))
		return EPropertyType::BoolProperty;

	if (IsClassName(L"StrProperty"))
		return EPropertyType::StrProperty;

	if (IsClassName(L"NameProperty"))
		return EPropertyType::NameProperty;

	if (IsClassName(L"TextProperty"))
		return EPropertyType::TextProperty;

	if (IsClassName(L"EnumProperty"))
		return EPropertyType::EnumProperty;

	if (IsClassName(L"InterfaceProperty"))
		return EPropertyType::InterfaceProperty;

	if (IsClassName(L"MapProperty"))
		return EPropertyType::MapProperty;

	if (IsClassName(L"ByteProperty"))
	{
		FByteProperty* ByteProp = static_cast<FByteProperty*>(Prop);
		UEnum* Enum = nullptr;
		DWORD Code = 0;

		if (TryReadBytePropertyEnum(ByteProp, Enum, Code) && Enum)
			return EPropertyType::EnumAsByteProperty;

		return EPropertyType::ByteProperty;
	}

	if (IsClassName(L"MulticastDelegateProperty") ||
		IsClassName(L"MulticastInlineDelegateProperty") ||
		IsClassName(L"MulticastSparseDelegateProperty"))
		return EPropertyType::MulticastDelegateProperty;

	if (IsClassName(L"DelegateProperty"))
		return EPropertyType::DelegateProperty;

	if (IsClassName(L"SoftObjectProperty") || IsClassName(L"SoftClassProperty"))
		return EPropertyType::SoftObjectProperty;

	if (IsClassName(L"WeakObjectProperty"))
		return EPropertyType::WeakObjectProperty;

	if (IsClassName(L"LazyObjectProperty"))
		return EPropertyType::LazyObjectProperty;

	if (IsClassName(L"SetProperty"))
		return EPropertyType::SetProperty;

	if (IsClassName(L"FieldPathProperty"))
		return EPropertyType::FieldPathProperty;

	switch (FieldClass->GetId())
	{
	case CASTCLASS_FObjectProperty:
	case CASTCLASS_FClassProperty:
	case CASTCLASS_FObjectPtrProperty:
	case CASTCLASS_FClassPtrProperty:
		return EPropertyType::ObjectProperty;
	case CASTCLASS_FStructProperty:
		return EPropertyType::StructProperty;
	case CASTCLASS_FInt8Property:
		return EPropertyType::Int8Property;
	case CASTCLASS_FInt16Property:
		return EPropertyType::Int16Property;
	case CASTCLASS_FIntProperty:
		return EPropertyType::IntProperty;
	case CASTCLASS_FInt64Property:
		return EPropertyType::Int64Property;
	case CASTCLASS_FUInt16Property:
		return EPropertyType::UInt16Property;
	case CASTCLASS_FUInt32Property:
		return EPropertyType::UInt32Property;
	case CASTCLASS_FUInt64Property:
		return EPropertyType::UInt64Property;
	case CASTCLASS_FArrayProperty:
		return EPropertyType::ArrayProperty;
	case CASTCLASS_FFloatProperty:
		return EPropertyType::FloatProperty;
	case CASTCLASS_FDoubleProperty:
	case CASTCLASS_FLargeWorldCoordinatesRealProperty:
		return EPropertyType::DoubleProperty;
	case CASTCLASS_FBoolProperty:
		return EPropertyType::BoolProperty;
	case CASTCLASS_FStrProperty:
		return EPropertyType::StrProperty;
	case CASTCLASS_FNameProperty:
		return EPropertyType::NameProperty;
	case CASTCLASS_FTextProperty:
		return EPropertyType::TextProperty;
	case CASTCLASS_FEnumProperty:
		return EPropertyType::EnumProperty;
	case CASTCLASS_FInterfaceProperty:
		return EPropertyType::InterfaceProperty;
	case CASTCLASS_FMapProperty:
		return EPropertyType::MapProperty;
	case CASTCLASS_FByteProperty:
	{
		FByteProperty* ByteProp = static_cast<FByteProperty*>(Prop);
		UEnum* Enum = nullptr;
		DWORD Code = 0;

		if (TryReadBytePropertyEnum(ByteProp, Enum, Code) && Enum)
			return EPropertyType::EnumAsByteProperty;

		return EPropertyType::ByteProperty;
	}
	case CASTCLASS_FMulticastDelegateProperty:
	case CASTCLASS_FMulticastInlineDelegateProperty:
	case CASTCLASS_FMulticastSparseDelegateProperty:
		return EPropertyType::MulticastDelegateProperty;
	case CASTCLASS_FDelegateProperty:
		return EPropertyType::DelegateProperty;
	case CASTCLASS_FSoftObjectProperty:
	case CASTCLASS_FSoftClassProperty:
		return EPropertyType::SoftObjectProperty;
	case CASTCLASS_FWeakObjectProperty:
		return EPropertyType::WeakObjectProperty;
	case CASTCLASS_FLazyObjectProperty:
		return EPropertyType::LazyObjectProperty;
	case CASTCLASS_FSetProperty:
		return EPropertyType::SetProperty;
	case CASTCLASS_FFieldPathProperty:
		return EPropertyType::FieldPathProperty;
	default:
		break;
	}

	return EPropertyType::Unknown;
}

static void LogNestedPropertyOffsetCandidates(const char* Kind, FProperty* Prop)
{
	static uint32_t LoggedScans = 0;
	if (LoggedScans >= 8)
		return;

	LoggedScans++;
	for (int Offset = 0x50; Offset <= 0xB0; Offset += 0x8)
	{
		void* Raw = nullptr;
		DWORD Code = 0;
		if (!TryReadPointerAt(Prop, Offset, Raw, Code))
		{
			UE_LOG("dump nested-candidate kind=%s prop=%p offset=0x%X fault=0x%08X", Kind, Prop, Offset, Code);
			continue;
		}

		const bool Readable = Raw && IsReadableMemory(Raw, 0x40);
		FFieldClass* FieldClass = nullptr;
		wchar_t ClassName[128]{};
		int ClassNameLength = 0;
		bool HasClassName = false;

		if (Readable &&
			TryReadFieldClass(static_cast<FProperty*>(Raw), FieldClass, Code) &&
			FieldClass &&
			IsReadableMemory(FieldClass, 0x10))
		{
			HasClassName = TryReadFieldClassName(FieldClass, ClassName, static_cast<int>(std::size(ClassName)), ClassNameLength, Code);
		}

		char NarrowClassName[128]{};
		if (HasClassName)
		{
			const auto CopyCount = std::min<int>(ClassNameLength, static_cast<int>(std::size(NarrowClassName)) - 1);
			for (int Index = 0; Index < CopyCount; Index++)
				NarrowClassName[Index] = ClassName[Index] <= 0x7F ? static_cast<char>(ClassName[Index]) : '?';
		}

		UE_LOG("dump nested-candidate kind=%s prop=%p offset=0x%X raw=%p readable=%d class=%s code=0x%08X",
			Kind,
			Prop,
			Offset,
			Raw,
			Readable ? 1 : 0,
			HasClassName ? NarrowClassName : "",
			Code);
	}
}

struct FPropertyData
{
	FProperty* Prop;
	uint16_t Index;
	uint8_t ArrayDim;
	FName Name;
	EPropertyType PropertyType;

	FPropertyData(FProperty* P, int Idx) :
		Prop(P),
		Index(Idx),
		ArrayDim(P->GetArrayDim()),
		Name(P->GetFName()),
		PropertyType(GetPropertyType(P))
	{
	}
};

void Dumper::ScanObjects()
{
	const int ObjectCount = ObjObjects::Num();
	UE_LOG("object-scan begin object_count=%d progress_interval=%u anomaly_limit=%u",
		ObjectCount,
		kObjectScanProgressInterval,
		kObjectScanAnomalyLogLimit);

	DWORD Code = 0;
	UClass* ClassByName = nullptr;
	UClass* ScriptStructByName = nullptr;
	UClass* EnumByName = nullptr;

	if (TryFindClassByName(L"Class", ClassByName, Code))
		UE_LOG("object-scan static-name class=Class ptr=%p readable=%d", ClassByName, IsReadableMemory(ClassByName, 0x40) ? 1 : 0);
	else
		UE_LOG("object-scan static-name class=Class fault code=0x%08X", Code);

	if (TryFindClassByName(L"ScriptStruct", ScriptStructByName, Code))
		UE_LOG("object-scan static-name class=ScriptStruct ptr=%p readable=%d", ScriptStructByName, IsReadableMemory(ScriptStructByName, 0x40) ? 1 : 0);
	else
		UE_LOG("object-scan static-name class=ScriptStruct fault code=0x%08X", Code);

	if (TryFindClassByName(L"Enum", EnumByName, Code))
		UE_LOG("object-scan static-name class=Enum ptr=%p readable=%d", EnumByName, IsReadableMemory(EnumByName, 0x40) ? 1 : 0);
	else
		UE_LOG("object-scan static-name class=Enum fault code=0x%08X", Code);

	uint32_t NullObjects = 0;
	uint32_t ObjectReadFaults = 0;
	uint32_t UnreadableObjects = 0;
	uint32_t ClassReadFaults = 0;
	uint32_t NullClasses = 0;
	uint32_t UnreadableClasses = 0;
	uint32_t ClassMatches = 0;
	uint32_t ScriptStructMatches = 0;
	uint32_t EnumMatches = 0;
	uint32_t AnomalyLogs = 0;
	uint32_t PropertyScanStructs = 0;

	std::vector<ChildPropertyOffsetScore> ChildPropertyScores;
	for (int Offset = kChildPropertyOffsetMin; Offset <= kChildPropertyOffsetMax; Offset += kChildPropertyOffsetStep)
		ChildPropertyScores.push_back({ Offset });

	constexpr int NextScanChildOffsets[] = { 0x50, 0x70, 0x78, 0x88 };
	std::vector<FieldNextOffsetScore> FieldNextScores;
	for (const auto ChildOffset : NextScanChildOffsets)
	{
		for (int NextOffset = kFieldNextOffsetMin; NextOffset <= kFieldNextOffsetMax; NextOffset += kFieldNextOffsetStep)
			FieldNextScores.push_back({ ChildOffset, NextOffset });
	}

	std::vector<FieldLayoutScore> FieldLayoutScores;
	for (const auto ChildOffset : NextScanChildOffsets)
	{
		for (int ClassOffset = kFieldClassOffsetMin; ClassOffset <= kFieldClassOffsetMax; ClassOffset += kFieldClassOffsetStep)
		{
			for (int NextOffset = kFieldNextOffsetMin; NextOffset <= 0x38; NextOffset += kFieldNextOffsetStep)
				FieldLayoutScores.push_back({ ChildOffset, ClassOffset, NextOffset });
		}
	}

	for (int Index = 0; Index < ObjectCount; ++Index)
	{
		if (Index % kObjectScanProgressInterval == 0)
		{
			UE_LOG("object-scan progress index=%d null_objects=%u object_faults=%u unreadable_objects=%u class_faults=%u unreadable_classes=%u class_matches=%u scriptstruct_matches=%u enum_matches=%u",
				Index,
				NullObjects,
				ObjectReadFaults,
				UnreadableObjects,
				ClassReadFaults,
				UnreadableClasses,
				ClassMatches,
				ScriptStructMatches,
				EnumMatches);
		}

		UObject* Object = nullptr;
		if (!TryGetObjectByIndex(Index, Object, Code))
		{
			ObjectReadFaults++;
			if (AnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("object-scan anomaly stage=get-object index=%d code=0x%08X", Index, Code);
			continue;
		}

		if (!Object)
		{
			NullObjects++;
			continue;
		}

		if (!IsReadableMemory(Object, 0x40))
		{
			UnreadableObjects++;
			if (AnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("object-scan anomaly stage=object-readable index=%d object=%p", Index, Object);
			continue;
		}

		UClass* ObjectClass = nullptr;
		if (!TryReadObjectClass(Object, ObjectClass, Code))
		{
			ClassReadFaults++;
			if (AnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("object-scan anomaly stage=read-class index=%d object=%p code=0x%08X", Index, Object, Code);
			continue;
		}

		if (!ObjectClass)
		{
			NullClasses++;
			if (AnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("object-scan anomaly stage=null-class index=%d object=%p", Index, Object);
			continue;
		}

		if (!IsReadableMemory(ObjectClass, 0x40))
		{
			UnreadableClasses++;
			if (AnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("object-scan anomaly stage=class-readable index=%d object=%p class=%p", Index, Object, ObjectClass);
			continue;
		}

		if (ClassByName && ObjectClass == ClassByName)
			ClassMatches++;
		else if (ScriptStructByName && ObjectClass == ScriptStructByName)
			ScriptStructMatches++;
		else if (EnumByName && ObjectClass == EnumByName)
			EnumMatches++;

		if ((ObjectClass == ClassByName || ObjectClass == ScriptStructByName) && PropertyScanStructs < kPropertyScanStructLimit)
		{
			PropertyScanStructs++;

			for (auto& Score : ChildPropertyScores)
			{
				void* HeadRaw = nullptr;
				if (!TryReadPointerAt(Object, Score.Offset, HeadRaw, Code))
				{
					Score.Faults++;
					continue;
				}

				if (!HeadRaw)
					continue;

				Score.NonNullHeads++;

				if (!IsReadableMemory(HeadRaw, 0x40))
					continue;

				Score.ReadableHeads++;

				const auto Depth = ScorePropertyChain(static_cast<FProperty*>(HeadRaw), Code);
				if (!Depth)
					continue;

				Score.PropertyClassHeads++;
				Score.ChainNodes += Depth;
				if (Depth > Score.ChainMax)
					Score.ChainMax = Depth;
			}

			for (auto& Score : FieldNextScores)
			{
				void* HeadRaw = nullptr;
				if (!TryReadPointerAt(Object, Score.ChildOffset, HeadRaw, Code))
				{
					Score.Faults++;
					continue;
				}

				if (!HeadRaw || !IsReadableMemory(HeadRaw, 0x40))
					continue;

				const auto Depth = ScorePropertyChainWithNextOffset(static_cast<FProperty*>(HeadRaw), Score.NextOffset, Code);
				if (!Depth)
					continue;

				Score.Heads++;
				Score.ChainNodes += Depth;
				if (Depth > Score.ChainMax)
					Score.ChainMax = Depth;
			}

			for (auto& Score : FieldLayoutScores)
			{
				void* HeadRaw = nullptr;
				if (!TryReadPointerAt(Object, Score.ChildOffset, HeadRaw, Code))
				{
					Score.Faults++;
					continue;
				}

				if (!HeadRaw || !IsReadableMemory(HeadRaw, 0x40))
					continue;

				const auto Depth = ScorePropertyChainWithFieldOffsets(static_cast<FProperty*>(HeadRaw), Score.ClassOffset, Score.NextOffset, Code);
				if (!Depth)
					continue;

				Score.Heads++;
				Score.ChainNodes += Depth;
				if (Depth > Score.ChainMax)
					Score.ChainMax = Depth;
			}
		}
	}

	UE_LOG("object-scan done object_count=%d null_objects=%u object_faults=%u unreadable_objects=%u class_faults=%u null_classes=%u unreadable_classes=%u class_matches=%u scriptstruct_matches=%u enum_matches=%u anomalies_logged=%u",
		ObjectCount,
		NullObjects,
		ObjectReadFaults,
		UnreadableObjects,
		ClassReadFaults,
		NullClasses,
		UnreadableClasses,
		ClassMatches,
		ScriptStructMatches,
		EnumMatches,
		AnomalyLogs);

	UE_LOG("object-scan child-property-offsets sampled_structs=%u range=0x%X..0x%X step=0x%X",
		PropertyScanStructs,
		kChildPropertyOffsetMin,
		kChildPropertyOffsetMax,
		kChildPropertyOffsetStep);

	for (const auto& Score : ChildPropertyScores)
	{
		UE_LOG("object-scan child-property-offset offset=0x%X nonnull=%u readable=%u property_heads=%u chain_nodes=%u chain_max=%u faults=%u",
			Score.Offset,
			Score.NonNullHeads,
			Score.ReadableHeads,
			Score.PropertyClassHeads,
			Score.ChainNodes,
			Score.ChainMax,
			Score.Faults);
	}

	UE_LOG("object-scan field-layout-offsets child_offsets=0x50,0x70,0x78,0x88 class_range=0x%X..0x%X next_range=0x%X..0x38",
		kFieldClassOffsetMin,
		kFieldClassOffsetMax,
		kFieldNextOffsetMin);

	for (const auto& Score : FieldLayoutScores)
	{
		if (!Score.Heads)
			continue;

		UE_LOG("object-scan field-layout child=0x%X class=0x%X next=0x%X heads=%u chain_nodes=%u chain_max=%u faults=%u",
			Score.ChildOffset,
			Score.ClassOffset,
			Score.NextOffset,
			Score.Heads,
			Score.ChainNodes,
			Score.ChainMax,
			Score.Faults);
	}

	UE_LOG("object-scan field-next-offsets child_offsets=0x50,0x70,0x78,0x88 next_range=0x%X..0x%X step=0x%X",
		kFieldNextOffsetMin,
		kFieldNextOffsetMax,
		kFieldNextOffsetStep);

	for (const auto& Score : FieldNextScores)
	{
		UE_LOG("object-scan field-next-offset child=0x%X next=0x%X heads=%u chain_nodes=%u chain_max=%u faults=%u",
			Score.ChildOffset,
			Score.NextOffset,
			Score.Heads,
			Score.ChainNodes,
			Score.ChainMax,
			Score.Faults);
	}
}

void Dumper::Run(ECompressionMethod CompressionMethod, const char* OutputPath)
{
	const char* Output = OutputPath && OutputPath[0] ? OutputPath : "Mappings.usmap";
	UE_LOG("dump phase=start output=%s compression=%d", Output, static_cast<int>(CompressionMethod));

	StreamWriter Buffer;
	phmap::parallel_flat_hash_map<FName, int> NameMap;

	std::vector<UEnum*> Enums;
	std::vector<UStruct*> Structs; // TODO: a better way than making this completely dynamic

	std::function<void(class FProperty*&, EPropertyType)> WritePropertyWrapper{}; // hacky.. i know

	auto WriteProperty = [&](FProperty*& Prop, EPropertyType Type)
	{
		auto WriteUnknown = [&]()
		{
			Buffer.Write(EPropertyType::Unknown);
		};

		auto WriteMappedType = [&](EPropertyType MappedType)
		{
			if (MappedType == EPropertyType::EnumAsByteProperty)
				Buffer.Write(EPropertyType::EnumProperty);
			else
				Buffer.Write(MappedType);
		};

		switch (Type)
		{
		case EPropertyType::EnumProperty:
		{
			auto EnumProp = static_cast<FEnumProperty*>(Prop);
			FProperty* Inner = nullptr;
			UEnum* Enum = nullptr;
			FName EnumName(0);
			DWORD Code = 0;

			if (!TryReadEnumPropertyUnderlying(EnumProp, Inner, Code) || !Inner || !IsReadableMemory(Inner, 0x40) ||
				!TryReadEnumPropertyEnum(EnumProp, Enum, Code) || !Enum || !IsReadableMemory(Enum, 0x40) ||
				!TryReadObjectFName(Enum, EnumName, Code))
			{
				UE_LOG("dump property nested-invalid kind=enum prop=%p inner=%p enum=%p code=0x%08X", Prop, Inner, Enum, Code);
				LogNestedPropertyOffsetCandidates("enum", Prop);
				WriteUnknown();
				break;
			}

			WriteMappedType(Type);
			auto InnerType = GetPropertyType(Inner);
			WritePropertyWrapper(Inner, InnerType);
			Buffer.Write(NameMap[EnumName]);

			break;
		}
		case EPropertyType::EnumAsByteProperty:
		{
			UEnum* Enum = nullptr;
			FName EnumName(0);
			DWORD Code = 0;

			if (!TryReadBytePropertyEnum(static_cast<FByteProperty*>(Prop), Enum, Code) || !Enum || !IsReadableMemory(Enum, 0x40) ||
				!TryReadObjectFName(Enum, EnumName, Code))
			{
				UE_LOG("dump property nested-invalid kind=byte-enum prop=%p enum=%p code=0x%08X", Prop, Enum, Code);
				Buffer.Write(EPropertyType::ByteProperty);
				break;
			}

			WriteMappedType(Type);
			Buffer.Write(EPropertyType::ByteProperty);
			Buffer.Write(NameMap[EnumName]);

			break;
		}
		case EPropertyType::StructProperty:
		{
			UScriptStruct* Struct = nullptr;
			FName StructName(0);
			DWORD Code = 0;

			if (!TryReadStructPropertyStruct(static_cast<FStructProperty*>(Prop), Struct, Code) || !Struct || !IsReadableMemory(Struct, 0x40) ||
				!TryReadObjectFName(Struct, StructName, Code))
			{
				UE_LOG("dump property nested-invalid kind=struct prop=%p struct=%p code=0x%08X", Prop, Struct, Code);
				WriteUnknown();
				break;
			}

			WriteMappedType(Type);
			Buffer.Write(NameMap[StructName]);
			break;
		}
		case EPropertyType::ArrayProperty:
		{
			FProperty* Inner = nullptr;
			DWORD Code = 0;

			WriteMappedType(Type);
			if (!TryReadArrayPropertyInner(static_cast<FArrayProperty*>(Prop), Inner, Code) || !Inner || !IsReadableMemory(Inner, 0x40))
			{
				UE_LOG("dump property nested-invalid kind=array prop=%p inner=%p code=0x%08X", Prop, Inner, Code);
				LogNestedPropertyOffsetCandidates("array", Prop);
				WriteUnknown();
				break;
			}

			auto InnerType = GetPropertyType(Inner);
			WritePropertyWrapper(Inner, InnerType);
			break;
		}
		case EPropertyType::SetProperty:
		{
			FProperty* Element = nullptr;
			DWORD Code = 0;

			WriteMappedType(Type);
			if (!TryReadSetPropertyElement(static_cast<FSetProperty*>(Prop), Element, Code) || !Element || !IsReadableMemory(Element, 0x40))
			{
				UE_LOG("dump property nested-invalid kind=set prop=%p element=%p code=0x%08X", Prop, Element, Code);
				LogNestedPropertyOffsetCandidates("set", Prop);
				WriteUnknown();
				break;
			}

			auto ElementType = GetPropertyType(Element);
			WritePropertyWrapper(Element, ElementType);
			break;
		}
		case EPropertyType::MapProperty:
		{
			FProperty* Inner = nullptr;
			FProperty* Value = nullptr;
			DWORD Code = 0;

			WriteMappedType(Type);
			if (!TryReadMapPropertyKey(static_cast<FMapProperty*>(Prop), Inner, Code) || !Inner || !IsReadableMemory(Inner, 0x40))
			{
				UE_LOG("dump property nested-invalid kind=map-key prop=%p inner=%p code=0x%08X", Prop, Inner, Code);
				LogNestedPropertyOffsetCandidates("map-key", Prop);
				WriteUnknown();
			}
			else
			{
				auto InnerType = GetPropertyType(Inner);
				WritePropertyWrapper(Inner, InnerType);
			}

			if (!TryReadMapPropertyValue(static_cast<FMapProperty*>(Prop), Value, Code) || !Value || !IsReadableMemory(Value, 0x40))
			{
				UE_LOG("dump property nested-invalid kind=map-value prop=%p value=%p code=0x%08X", Prop, Value, Code);
				LogNestedPropertyOffsetCandidates("map-value", Prop);
				WriteUnknown();
			}
			else
			{
				auto ValueType = GetPropertyType(Value);
				WritePropertyWrapper(Value, ValueType);
			}

			break;
		}
		default:
			WriteMappedType(Type);
			break;
		}
	};

	WritePropertyWrapper = WriteProperty;

	uint32_t ObjectVisitCount = 0;
	uint32_t StructCandidateCount = 0;
	uint32_t EnumCandidateCount = 0;
	uint32_t CollectedPropertyNameCount = 0;
	uint32_t CollectedEnumNameCount = 0;
	uint32_t SkippedNullObjects = 0;
	uint32_t SkippedUnreadableObjects = 0;
	uint32_t ObjectReadFaults = 0;
	uint32_t ClassReadFaults = 0;
	uint32_t SkippedUnreadableClasses = 0;
	uint32_t SkippedAnomalyLogs = 0;

	DWORD Code = 0;
	UClass* ClassType = nullptr;
	UClass* ScriptStructType = nullptr;
	UClass* EnumType = nullptr;

	if (!TryFindClassByName(L"Class", ClassType, Code) || !ClassType)
	{
		UE_LOG("dump abort reason=missing-class-type name=Class code=0x%08X", Code);
		return;
	}

	if (!TryFindClassByName(L"ScriptStruct", ScriptStructType, Code) || !ScriptStructType)
	{
		UE_LOG("dump abort reason=missing-class-type name=ScriptStruct code=0x%08X", Code);
		return;
	}

	if (!TryFindClassByName(L"Enum", EnumType, Code) || !EnumType)
	{
		UE_LOG("dump abort reason=missing-class-type name=Enum code=0x%08X", Code);
		return;
	}

	UE_LOG("dump class-types class=%p scriptstruct=%p enum=%p", ClassType, ScriptStructType, EnumType);

	UE_LOG("dump phase=collect-types begin object_count=%d", ObjObjects::Num());

	const int ObjectCount = ObjObjects::Num();
	for (int ObjectIndex = 0; ObjectIndex < ObjectCount; ++ObjectIndex)
	{
		ObjectVisitCount++;

		if (ObjectVisitCount % 5000 == 0)
		{
			UE_LOG("dump phase=collect-types progress visited=%u index=%d structs=%zu enums=%zu names=%zu",
				ObjectVisitCount,
				ObjectIndex,
				Structs.size(),
				Enums.size(),
				NameMap.size());
		}

		UObject* Object = nullptr;
		if (!TryGetObjectByIndex(ObjectIndex, Object, Code))
		{
			ObjectReadFaults++;
			if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("dump skip stage=get-object index=%d code=0x%08X", ObjectIndex, Code);
			continue;
		}

		if (!Object)
		{
			SkippedNullObjects++;
			continue;
		}

		if (!IsReadableMemory(Object, 0x40))
		{
			SkippedUnreadableObjects++;
			if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("dump skip stage=object-readable index=%d object=%p", ObjectIndex, Object);
			continue;
		}

		UClass* ObjectClass = nullptr;
		if (!TryReadObjectClass(Object, ObjectClass, Code))
		{
			ClassReadFaults++;
			if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("dump skip stage=read-class index=%d object=%p code=0x%08X", ObjectIndex, Object, Code);
			continue;
		}

		if (ObjectClass && !IsReadableMemory(ObjectClass, 0x40))
		{
			SkippedUnreadableClasses++;
			if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
				UE_LOG("dump skip stage=class-readable index=%d object=%p class=%p", ObjectIndex, Object, ObjectClass);
			continue;
		}

		if (ObjectClass == ClassType ||
		ObjectClass == ScriptStructType)
		{
				auto Struct = static_cast<UStruct*>(Object);
				bool IncludeStruct = true;

				FName StructName(0);
				if (!TryReadObjectFName(Struct, StructName, Code))
				{
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=struct-name index=%d struct=%p code=0x%08X", ObjectIndex, Struct, Code);
					continue;
				}

				NameMap.insert_or_assign(StructName, 0);

				UStruct* Super = nullptr;
				if (TryReadStructSuper(Struct, Super, Code) && Super)
				{
					FName SuperName(0);
					if (TryReadObjectFName(Super, SuperName, Code))
					{
						if (!NameMap.contains(SuperName))
							NameMap.insert_or_assign(SuperName, 0);
					}
					else
					{
						IncludeStruct = false;
						if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
							UE_LOG("dump skip stage=super-name index=%d struct=%p super=%p code=0x%08X", ObjectIndex, Struct, Super, Code);
					}
				}
				else if (Code)
				{
					IncludeStruct = false;
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=struct-super index=%d struct=%p code=0x%08X", ObjectIndex, Struct, Code);
				}

				if (!IncludeStruct)
					continue;

				FProperty* Props = nullptr;
				if (!TryReadStructChildProperties(Struct, Props, Code))
				{
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=child-properties index=%d struct=%p code=0x%08X", ObjectIndex, Struct, Code);
					continue;
				}

				uint32_t PropertyWalkCount = 0;

				while (Props)
				{
					if (!IsReadableMemory(Props, 0x40))
					{
						IncludeStruct = false;
						if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
							UE_LOG("dump skip stage=property-readable index=%d struct=%p property=%p", ObjectIndex, Struct, Props);
						break;
					}

					if (++PropertyWalkCount > kMaxPropertyWalk)
					{
						IncludeStruct = false;
						UE_LOG("dump phase=collect-types warning=property-walk-limit struct=%p", Struct);
						break;
					}

					FName PropertyName(0);
					if (!TryReadPropertyFName(Props, PropertyName, Code))
					{
						IncludeStruct = false;
						if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
							UE_LOG("dump skip stage=property-name index=%d struct=%p property=%p code=0x%08X", ObjectIndex, Struct, Props, Code);
						break;
					}

					NameMap.insert_or_assign(PropertyName, 0);
					CollectedPropertyNameCount++;

					FProperty* Next = nullptr;
					if (!TryReadPropertyNext(Props, Next, Code))
					{
						IncludeStruct = false;
						if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
							UE_LOG("dump skip stage=property-next index=%d struct=%p property=%p code=0x%08X", ObjectIndex, Struct, Props, Code);
						break;
					}
					Props = Next;
				}

				if (!IncludeStruct)
					continue;

				Structs.push_back(Struct);
				StructCandidateCount++;
			}
			else if (ObjectClass == EnumType)
			{
				auto Enum = static_cast<UEnum*>(Object);
				bool IncludeEnum = true;

				FName EnumName(0);
				if (!TryReadObjectFName(Enum, EnumName, Code))
				{
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=enum-name index=%d enum=%p code=0x%08X", ObjectIndex, Enum, Code);
					continue;
				}

				NameMap.insert_or_assign(EnumName, 0);

				UEnum::EnumNameMap* EnumNames = nullptr;
				if (!TryReadEnumNames(Enum, EnumNames, Code))
				{
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=enum-names index=%d enum=%p code=0x%08X", ObjectIndex, Enum, Code);
					continue;
				}

				int EnumNameCount = 0;
				if (!TryReadEnumNameCount(EnumNames, EnumNameCount, Code))
				{
					if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
						UE_LOG("dump skip stage=enum-name-count index=%d enum=%p names=%p code=0x%08X", ObjectIndex, Enum, EnumNames, Code);
					continue;
				}

				for (auto i = 0; i < EnumNameCount; i++)
				{
					FName EnumValueName(0);
					if (!TryReadEnumNameKey(EnumNames, i, EnumValueName, Code))
					{
						IncludeEnum = false;
						if (SkippedAnomalyLogs++ < kObjectScanAnomalyLogLimit)
							UE_LOG("dump skip stage=enum-name-key index=%d enum=%p names=%p enum_index=%d code=0x%08X", ObjectIndex, Enum, EnumNames, i, Code);
						break;
					}

					NameMap.insert_or_assign(EnumValueName.GetNumber(), 0);
					CollectedEnumNameCount++;
				}

				if (!IncludeEnum)
					continue;

				Enums.push_back(Enum);
				EnumCandidateCount++;
			}
	}

	UE_LOG("dump phase=collect-types done visited=%u struct_candidates=%u enum_candidates=%u property_names=%u enum_names=%u names=%zu null_objects=%u object_faults=%u unreadable_objects=%u class_faults=%u unreadable_classes=%u skip_logs=%u",
		ObjectVisitCount,
		StructCandidateCount,
		EnumCandidateCount,
		CollectedPropertyNameCount,
		CollectedEnumNameCount,
		NameMap.size(),
		SkippedNullObjects,
		ObjectReadFaults,
		SkippedUnreadableObjects,
		ClassReadFaults,
		SkippedUnreadableClasses,
		SkippedAnomalyLogs);

	Buffer.Write<int>(NameMap.size());

	int CurrentNameIndex = 0;
	uint32_t InvalidNameCount = 0;
	uint32_t InvalidNameLogCount = 0;

	UE_LOG("dump phase=write-names begin count=%zu", NameMap.size());

	for (auto&& N : NameMap)
	{
		if (CurrentNameIndex % 10000 == 0)
		{
			UE_LOG("dump phase=write-names progress index=%d", CurrentNameIndex);
		}

		NameMap[N.first] = CurrentNameIndex;

		std::string Name;
		const wchar_t* WideName = nullptr;
		int WideNameLength = 0;
		if (TryFNameToWide(N.first, WideName, WideNameLength, Code) && WideName && WideNameLength > 0)
		{
			Name.reserve(static_cast<size_t>(WideNameLength));
			for (int i = 0; i < WideNameLength; ++i)
			{
				const wchar_t Ch = WideName[i];
				Name.push_back(Ch >= 0 && Ch <= 0x7f ? static_cast<char>(Ch) : '?');
			}
		}
		else
		{
			auto FallbackName = N.first;
			const auto FallbackNumber = FallbackName.GetNumber();
			InvalidNameCount++;
			if (InvalidNameLogCount++ < kObjectScanAnomalyLogLimit)
				UE_LOG("dump fallback stage=fname-to-string index=%d fname_number=%u code=0x%08X",
					CurrentNameIndex,
					FallbackNumber,
					Code);

			char Fallback[64]{};
			snprintf(Fallback, sizeof(Fallback), "InvalidName_%u", FallbackNumber);
			Name = Fallback;
		}

		std::string_view NameView = Name;

		auto Find = Name.find("::");
		if (Find != std::string::npos)
		{
			NameView = NameView.substr(Find + 2);
		}

		Buffer.Write<uint16_t>(NameView.length());
		Buffer.WriteString(NameView);

		CurrentNameIndex++;
	}

	UE_LOG("dump phase=write-names done count=%d invalid_names=%u", CurrentNameIndex, InvalidNameCount);

	Buffer.Write<uint32_t>(Enums.size());

	UE_LOG("dump phase=write-enums begin count=%zu", Enums.size());

	uint32_t CurrentEnumIndex = 0;
	for (auto Enum : Enums)
	{
		if (CurrentEnumIndex % 1000 == 0)
		{
			UE_LOG("dump phase=write-enums progress index=%u enum=%p", CurrentEnumIndex, Enum);
		}

		Buffer.Write(NameMap[Enum->GetFName()]);

		auto& EnumNames = Enum->Names();
		auto EnumNameCount = EnumNames.Num();
		if (EnumNameCount > UINT16_MAX)
			EnumNameCount = UINT16_MAX;
		Buffer.Write<uint16_t>(EnumNameCount);

		for (int i = 0; i < EnumNameCount; i++)
		{
			Buffer.Write<int>(NameMap[EnumNames[i].Key]);
		}

		CurrentEnumIndex++;
	}

	UE_LOG("dump phase=write-enums done count=%u", CurrentEnumIndex);

	Buffer.Write<uint32_t>(Structs.size());

	UE_LOG("dump phase=write-structs begin count=%zu", Structs.size());

	uint32_t CurrentStructIndex = 0;
	uint32_t TotalSerializablePropertyCount = 0;

	for (auto Struct : Structs)
	{
		if (CurrentStructIndex % 500 == 0)
		{
			UE_LOG("dump phase=write-structs progress index=%u struct=%p total_props=%u",
				CurrentStructIndex,
				Struct,
				TotalSerializablePropertyCount);
		}

		Buffer.Write(NameMap[Struct->GetFName()]);
		Buffer.Write<int32_t>(Struct->Super() ? NameMap[Struct->Super()->GetFName()] : 0xffffffff);

		std::vector<FPropertyData> Properties;

		auto Props = Struct->ChildProperties();
		uint16_t PropCount = 0;
		uint16_t SerializablePropCount = 0;
		uint32_t PropertyWalkCount = 0;

		while (Props)
		{
			if (++PropertyWalkCount > kMaxPropertyWalk)
			{
				UE_LOG("dump phase=write-structs warning=property-walk-limit struct=%p index=%u",
					Struct,
					CurrentStructIndex);
				break;
			}

			FPropertyData Data(Props, PropCount);
			Properties.push_back(Data);
			Props = static_cast<FProperty*>(Props->GetNext());

			PropCount += Data.ArrayDim;
			SerializablePropCount++;
		}

		Buffer.Write(PropCount);
		Buffer.Write(SerializablePropCount);

		for (auto P : Properties)
		{
			Buffer.Write<uint16_t>(P.Index);
			Buffer.Write(P.ArrayDim);
			Buffer.Write(NameMap[P.Name]);

			WriteProperty(P.Prop, P.PropertyType);
			TotalSerializablePropertyCount++;
		}

		CurrentStructIndex++;
	}

	UE_LOG("dump phase=write-structs done count=%u total_props=%u", CurrentStructIndex, TotalSerializablePropertyCount);

	std::vector<uint8_t> UsmapData;

	UE_LOG("dump phase=compress begin compression=%d buffer_size=%u",
		static_cast<int>(CompressionMethod),
		Buffer.Size());

	switch (CompressionMethod)
	{
	case ECompressionMethod::Oodle:
	{
		UsmapData = Oodle::Compress(Buffer.GetBuffer());
		break;
	}
	default:
	{
		std::string UncompressedStream = Buffer.GetBuffer().str();
		UsmapData.resize(UncompressedStream.size());
		memcpy(UsmapData.data(), UncompressedStream.data(), UsmapData.size());
	}
	}

	UE_LOG("dump phase=compress done compressed_size=%zu decompressed_size=%u",
		UsmapData.size(),
		Buffer.Size());

	UE_LOG("dump phase=write-file begin path=%s", Output);

	auto FileOutput = FileWriter(Output);

	FileOutput.Write<uint16_t>(0x30C4); //magic
	FileOutput.Write<uint8_t>(kUsmapVersionLargeEnums); //version
	FileOutput.Write<int32_t>(0); //bHasVersioning
	FileOutput.Write(CompressionMethod); //compression
	FileOutput.Write<uint32_t>(UsmapData.size()); //compressed size
	FileOutput.Write<uint32_t>(Buffer.Size()); //decompressed size

	FileOutput.Write(UsmapData.data(), UsmapData.size());

	UE_LOG("dump phase=write-file done path=%s bytes=%zu", Output, UsmapData.size());
}
