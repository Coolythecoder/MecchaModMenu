#pragma once

#include "app.h"
#include "unrealTypes.h"
#include "scanning.h"

/*
* The idea here is to make something that can easily be overriden for engine or game versions with different types
* that need to be overriden or handled differently.
*/

struct IUnrealVersion
{
private:
	static bool TryDynamicOffsets();

public:

	template <typename Version>
	static bool InitTypes()
	{
		uintptr_t GObjectsAddy = 0;
		int GObjectsPatternIndex = 0;

		for (auto Scan : Version::GetGObjectsPatterns())
		{
			auto Candidate = Scan->TryFind();
			UE_LOG("scan GObjects[%d] result=0x%p", GObjectsPatternIndex, reinterpret_cast<void*>(Candidate));
			GObjectsPatternIndex++;

			GObjectsAddy = Candidate;

			if (GObjectsAddy)
				break;
		}

		if (!GObjectsAddy)
		{
			UE_LOG("Could not find the address for GObjects. Try overriding it or adding the correct sig for it.");
			return false;
		}

		ObjObjects::SetInstance(GObjectsAddy);
		if (!ObjObjects::LooksSane())
		{
			UE_LOG("Rejected GObjects address 0x%p because object array sanity checks failed.", reinterpret_cast<void*>(GObjectsAddy));
			return false;
		}
		UE_LOG("Accepted GObjects address 0x%p with object count %d", reinterpret_cast<void*>(GObjectsAddy), ObjObjects::Num());

		uintptr_t FNameStringAddy = 0;
		int FNamePatternIndex = 0;

		for (auto Scan : Version::GetFNameStringPatterns())
		{
			auto Candidate = Scan->TryFind();
			UE_LOG("scan FNameToString[%d] result=0x%p", FNamePatternIndex, reinterpret_cast<void*>(Candidate));
			FNamePatternIndex++;

			FNameStringAddy = Candidate;

			if (FNameStringAddy)
				break;
		}

		if (!FNameStringAddy)
		{
			UE_LOG("Could not find the address for FNameToString. Try overriding it or adding the correct sig for it.");
			return false;
		}

		if (!Research::IsExecutableAddress(FNameStringAddy))
		{
			UE_LOG("Rejected FNameToString address 0x%p because it is not executable memory.", reinterpret_cast<void*>(FNameStringAddy));
			return false;
		}

		UE_LOG("Accepted FNameToString address 0x%p", reinterpret_cast<void*>(FNameStringAddy));
		FNameToString = (_FNameToString)FNameStringAddy;

		using UObjectImpl = Version::Offsets::UObject;
		using UStructImpl = Version::Offsets::UStruct;

		UObject::NameOffset = UObjectImpl::NameOffset;
		FName::IsOptimized = Version::HasOptimizedFName;
		FProperty::FPropertySize = Version::FPropertySize;
		FField::ClassPrivateOffset = Version::FFieldClassPrivateOffset;
		FField::NextOffset = Version::FFieldNextOffset;
		FField::NameOffset = Version::FFieldNameOffset;
		FField::FlagsOffset = Version::FFieldFlagsOffset;
		FProperty::ArrayDimOffset = Version::FPropertyArrayDimOffset;
		FStructProperty::StructOffset = Version::FStructPropertyStructOffset;
		FByteProperty::EnumOffset = Version::FBytePropertyEnumOffset;
		FArrayProperty::InnerOffset = Version::FArrayPropertyInnerOffset;
		FSetProperty::ElementPropOffset = Version::FSetPropertyElementPropOffset;
		FMapProperty::KeyPropOffset = Version::FMapPropertyKeyPropOffset;
		FMapProperty::ValuePropOffset = Version::FMapPropertyValuePropOffset;
		FEnumProperty::UnderlyingPropOffset = Version::FEnumPropertyUnderlyingPropOffset;
		FEnumProperty::EnumOffset = Version::FEnumPropertyEnumOffset;
		FFieldClass::NameOffset = Version::FFieldClassNameOffset;
		FFieldClass::CastFlagsOffset = Version::FFieldClassCastFlagsOffset;
		UE_LOG("field-layout ClassPrivate=0x%X Next=0x%X Name=0x%X Flags=0x%X ArrayDim=0x%X FPropertySize=0x%X Struct=0x%X ByteEnum=0x%X ArrayInner=0x%X SetElement=0x%X MapKey=0x%X MapValue=0x%X EnumUnderlying=0x%X Enum=0x%X FieldClassName=0x%X FieldClassCastFlags=0x%X",
			FField::ClassPrivateOffset,
			FField::NextOffset,
			FField::NameOffset,
			FField::FlagsOffset,
			FProperty::ArrayDimOffset,
			FProperty::FPropertySize,
			FStructProperty::StructOffset,
			FByteProperty::EnumOffset,
			FArrayProperty::InnerOffset,
			FSetProperty::ElementPropOffset,
			FMapProperty::KeyPropOffset,
			FMapProperty::ValuePropOffset,
			FEnumProperty::UnderlyingPropOffset,
			FEnumProperty::EnumOffset,
			FFieldClass::NameOffset,
			FFieldClass::CastFlagsOffset);

		if (!TryDynamicOffsets())
		{
			UE_LOG("Could not grab dynamic offsets. Just gonna use the hardcoded ones.");

			UObject::ClassOffset = UObjectImpl::ClassOffset;
			UObject::OuterOffset = UObjectImpl::OuterOffset;

			UStruct::SuperOffset = UStructImpl::SuperOffset;
			UStruct::ChildPropertiesOffset = UStructImpl::ChildPropertiesOffset;
		}

		return true;
	}
};

/*
* Yes, it's true, UObject should pretty much always be the same across all games,
* however there are some games that use a custom UObject, so should they ever need mappings,
* this design pattern makes it easier to override the offsets.
*/

struct UnrealVersionBase : IUnrealVersion
{
	static constexpr int FPropertySize = 0x70;
	static constexpr int FPropertyArrayDimOffset = 0x30;
	static constexpr int FStructPropertyStructOffset = 0x70;
	static constexpr int FBytePropertyEnumOffset = 0x70;
	static constexpr int FArrayPropertyInnerOffset = 0x78;
	static constexpr int FSetPropertyElementPropOffset = 0x70;
	static constexpr int FMapPropertyKeyPropOffset = 0x70;
	static constexpr int FMapPropertyValuePropOffset = 0x78;
	static constexpr int FEnumPropertyUnderlyingPropOffset = 0x70;
	static constexpr int FEnumPropertyEnumOffset = 0x78;
	static constexpr int FFieldClassPrivateOffset = 0x8;
	static constexpr int FFieldNextOffset = 0x18;
	static constexpr int FFieldNameOffset = 0x20;
	static constexpr int FFieldFlagsOffset = 0x28;
	static constexpr int FFieldClassNameOffset = 0x0;
	static constexpr int FFieldClassCastFlagsOffset = 0x8;
	static constexpr bool HasOptimizedFName = false;

	struct Offsets
	{
		struct UObject
		{
			static constexpr int NameOffset = 0x18;
			static constexpr int ClassOffset = 0x10;
			static constexpr int OuterOffset = 0x20;
		};

		struct UStruct
		{
			static constexpr int SuperOffset = 0x40;
			static constexpr int ChildPropertiesOffset = 0x50;
		};
	};

	static std::vector<std::shared_ptr<IScanObject>> GetFNameStringPatterns()
	{
		return
		{
			std::make_shared<PatternScanObject>("E8 ? ? ? ? 83 7D C8 00 48 8D 15 ? ? ? ? 0F 5A DE", 1, true),
			std::make_shared<PatternScanObject>("E8 ? ? ? ? 48 8B 4C 24 ? 8B FD 48 85 C9", 1, true),// 4.12 - 5.0 EA
			std::make_shared<PatternScanObject>("E8 ? ? ? ? BD 01 00 00 00 41 39 6E ? 0F 8E", 1, true),// 4.25+ Backup
			std::make_shared<StringRefScanObject<std::wstring>>(
				L"%s %s SetTimer passed a negative or zero time. The associated timer may fail to be created/fire! If using InitialStartDelayVariance, be sure it is smaller than (Time + InitialStartDelay).",
				true, 1, true, 0xE8)
		};
	}


	static std::vector<std::shared_ptr<IScanObject>> GetGObjectsPatterns()
	{
		return
		{
			std::make_shared<PatternScanObject>("48 89 05 ? ? ? ? E8 ? ? ? ? ? ? ? 0F 84", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C 07 48 85 C9 74 20", 3, true),
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C", 3, true),
			std::make_shared<PatternScanObject>("48 03 ? ? ? ? ? ? ? ? ? ? 48 8B 10 48 85 D2 74 07", 3, true)

		};
	}
};

/*
* Use this if the games engine has UE_FNAME_OUTLINE_NUMBER defined as 1
*/
struct Version_OptimizedFName : UnrealVersionBase
{
	static constexpr int FPropertySize = 0x70;
	static constexpr bool HasOptimizedFName = true;
};

struct Version_FortniteLatest : Version_OptimizedFName
{
	static std::vector<std::shared_ptr<IScanObject>> GetGObjectsPatterns()
	{
		return
		{
			std::make_shared<PatternScanObject>("48 8B 05 ? ? ? ? 48 8B 0C C8", 3, true)
		};
	}
};
