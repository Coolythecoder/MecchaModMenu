#include "pch.h"

#include "app.h"
#include "dumper.h"

constexpr bool OpenConsole = false;

static void RunMain(HMODULE Module)
{
	if constexpr (OpenConsole)
	{
		AllocConsole();
		FILE* f;
		freopen_s(&f, "CONOUT$", "w", stdout);
	}

	UE_LOG("Unreal Mappings Dumper created by OutTheShade");

	if (!App::Init())
	{
		UE_LOG("Failed to initialize the dumper. Returning.");
		return;
	}

	if (Research::GetConfig().Mode == Research::DumperMode::Probe)
	{
		UE_LOG("Probe-only mode completed. Skipping usmap generation.");
		return;
	}

	if (Research::GetConfig().Mode == Research::DumperMode::ObjectScan)
	{
		Dumper::ScanObjects();
		UE_LOG("Object scan mode completed. Skipping usmap generation.");
		return;
	}

	auto Start = std::chrono::steady_clock::now();

	Dumper::Run(ECompressionMethod::None, Research::GetConfig().OutputPath.string().c_str());

	auto End = std::chrono::steady_clock::now();

	UE_LOG("Successfully generated mappings file in %.02f ms", (End - Start).count() / 1000000.);
}

void WINAPI Main(HMODULE Module)
{
	Research::LoadConfig(Module);
	__try
	{
		RunMain(Module);
	}
	__except (Research::LogSehException(GetExceptionInformation()))
	{
	}
	FreeLibraryAndExitThread(Module, NULL);
}

BOOL APIENTRY DllMain(
	HMODULE hModule,
	DWORD  ul_reason_for_call,
	LPVOID lpReserved
)
{
	switch (ul_reason_for_call)
	{
	case DLL_PROCESS_ATTACH:
		CloseHandle(CreateThread(nullptr, 0, (LPTHREAD_START_ROUTINE)Main, hModule, 0, nullptr));
	case DLL_THREAD_ATTACH:
	case DLL_THREAD_DETACH:
	case DLL_PROCESS_DETACH:
		break;
	}
	return TRUE;
}
