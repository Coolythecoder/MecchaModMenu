#include "pch.h"

#include "unrealVersion.h"

#pragma comment(lib, "Version.lib")

#define SCAN_LIMIT 0x300

#define SCAN_FOR_MEMBER_OFFSET(obj, member, outOffset) \
	for (uint8_t* i = (uint8_t*)obj; ; i++)\
	{\
		auto Count = i - (uint8_t*)obj;\
		if (Count >= SCAN_LIMIT)\
			return false;\
		\
		if (*(uintptr_t*)i == (uintptr_t)member)\
		{\
			outOffset = Count;\
			break;\
		}\
	}\


//this is super unsafe but hopefully stackoverflow comes in clutch https://stackoverflow.com/a/42389638
bool IUnrealVersion::TryDynamicOffsets()
{
	__try
	{
		auto UClassPtr = ObjObjects::FindObjectByName<UClass>(L"Class");
		auto UObjectPtr = ObjObjects::FindObjectByName<UClass>(L"Object");
		auto ActorPtr = ObjObjects::FindObjectByName<UClass>(L"Actor");
		auto EnginePtr = ObjObjects::FindObjectByName(L"/Script/Engine");

		if (!UClassPtr or !UObjectPtr or !ActorPtr or !EnginePtr)
		{
			UE_LOG("dynamic-offsets missing anchors class=%p object=%p actor=%p engine=%p", UClassPtr, UObjectPtr, ActorPtr, EnginePtr);
			return false;
		}

		SCAN_FOR_MEMBER_OFFSET(UObjectPtr, UClassPtr, UObject::ClassOffset);

		if (!UObject::ClassOffset)
			return false;
		UE_LOG("dynamic-offset ClassOffset=0x%X", UObject::ClassOffset);

		SCAN_FOR_MEMBER_OFFSET(ActorPtr, UObjectPtr, UStruct::SuperOffset);

		if (!UStruct::SuperOffset)
			return false;
		UE_LOG("dynamic-offset SuperOffset=0x%X", UStruct::SuperOffset);

		UStruct::ChildPropertiesOffset = UStruct::SuperOffset + (sizeof(void*) * 2);
		UE_LOG("dynamic-offset ChildPropertiesOffset=0x%X", UStruct::ChildPropertiesOffset);

		SCAN_FOR_MEMBER_OFFSET(ActorPtr, EnginePtr, UObject::OuterOffset);

		if (!UObject::OuterOffset)
			return false;
		UE_LOG("dynamic-offset OuterOffset=0x%X", UObject::OuterOffset);
	}
	__except (Research::LogSehException(GetExceptionInformation()))
	{
		return false;
	}

	return true;
}
