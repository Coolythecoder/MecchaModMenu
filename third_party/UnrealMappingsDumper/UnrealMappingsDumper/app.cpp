#include "pch.h"

#include "app.h"
#include "unrealVersion.h"

struct GameInstance
{
	std::filesystem::path GamePath;
	float Version = 0;
};

namespace
{
	Research::Config GConfig;

	static std::string Trim(std::string Value)
	{
		auto NotSpace = [](unsigned char Ch) { return !std::isspace(Ch); };
		Value.erase(Value.begin(), std::find_if(Value.begin(), Value.end(), NotSpace));
		Value.erase(std::find_if(Value.rbegin(), Value.rend(), NotSpace).base(), Value.end());
		return Value;
	}

	static std::string Lower(std::string Value)
	{
		std::transform(Value.begin(), Value.end(), Value.begin(), [](unsigned char Ch) { return static_cast<char>(std::tolower(Ch)); });
		return Value;
	}

	static void ApplyConfigValue(const std::string& Key, const std::string& Value)
	{
		auto NormalizedKey = Lower(Trim(Key));
		auto TrimmedValue = Trim(Value);
		if (NormalizedKey == "mode")
		{
			auto Mode = Lower(TrimmedValue);
			if (Mode == "dump")
				GConfig.Mode = Research::DumperMode::Dump;
			else if (Mode == "objectscan" || Mode == "object-scan")
				GConfig.Mode = Research::DumperMode::ObjectScan;
			else
				GConfig.Mode = Research::DumperMode::Probe;
		}
		else if (NormalizedKey == "log")
		{
			GConfig.LogPath = TrimmedValue;
		}
		else if (NormalizedKey == "output")
		{
			GConfig.OutputPath = TrimmedValue;
		}
	}

	static void EnsureLogDirectory()
	{
		if (GConfig.LogPath.empty())
			GConfig.LogPath = GConfig.DllDirectory / "UnrealMappingsDumper.log";

		std::error_code Ec;
		std::filesystem::create_directories(GConfig.LogPath.parent_path(), Ec);
	}

	static void WriteLogLine(const std::string& Line)
	{
		EnsureLogDirectory();
		auto Handle = CreateFileW(
			GConfig.LogPath.wstring().c_str(),
			FILE_APPEND_DATA,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
			nullptr,
			OPEN_ALWAYS,
			FILE_ATTRIBUTE_NORMAL,
			nullptr);
		if (Handle == INVALID_HANDLE_VALUE)
			return;

		std::string Output = "[=] " + Line + "\r\n";
		DWORD Written = 0;
		WriteFile(Handle, Output.data(), static_cast<DWORD>(Output.size()), &Written, nullptr);
		CloseHandle(Handle);
	}
}

namespace Research
{
	void LoadConfig(HMODULE Module)
	{
		wchar_t DllPath[MAX_PATH];
		GetModuleFileNameW(Module, DllPath, MAX_PATH);

		GConfig = {};
		GConfig.DllPath = DllPath;
		GConfig.DllDirectory = GConfig.DllPath.parent_path();
		GConfig.ConfigPath = GConfig.DllDirectory / "UnrealMappingsDumper.config";
		GConfig.LogPath = GConfig.DllDirectory / "UnrealMappingsDumper.log";
		GConfig.OutputPath = GConfig.DllDirectory / "Mappings.usmap";

		std::ifstream ConfigFile(GConfig.ConfigPath);
		std::string Line;
		while (std::getline(ConfigFile, Line))
		{
			auto Comment = Line.find('#');
			if (Comment != std::string::npos)
				Line.erase(Comment);
			auto Separator = Line.find('=');
			if (Separator == std::string::npos)
				continue;
			ApplyConfigValue(Line.substr(0, Separator), Line.substr(Separator + 1));
		}

		EnsureLogDirectory();
		UE_LOG("config mode=%s dll=%s config=%s log=%s output=%s",
			ModeName(),
			GConfig.DllPath.string().c_str(),
			GConfig.ConfigPath.string().c_str(),
			GConfig.LogPath.string().c_str(),
			GConfig.OutputPath.string().c_str());
	}

	const Config& GetConfig()
	{
		return GConfig;
	}

	const char* ModeName()
	{
		switch (GConfig.Mode)
		{
		case DumperMode::Dump:
			return "dump";
		case DumperMode::ObjectScan:
			return "objectscan";
		default:
			return "probe";
		}
	}

	void LogV(const char* str, va_list args)
	{
		va_list ConsoleArgs;
		va_copy(ConsoleArgs, args);
		printf("[=] ");
		vprintf(str, ConsoleArgs);
		printf("\n");
		va_end(ConsoleArgs);

		va_list SizeArgs;
		va_copy(SizeArgs, args);
		const auto Size = vsnprintf(nullptr, 0, str, SizeArgs);
		va_end(SizeArgs);
		if (Size < 0)
			return;

		std::vector<char> Buffer(static_cast<size_t>(Size) + 1);
		va_list FileArgs;
		va_copy(FileArgs, args);
		vsnprintf(Buffer.data(), Buffer.size(), str, FileArgs);
		va_end(FileArgs);
		WriteLogLine(std::string(Buffer.data(), static_cast<size_t>(Size)));
	}

	bool IsExecutableAddress(uintptr_t Address)
	{
		MEMORY_BASIC_INFORMATION Info{};
		if (!Address || !VirtualQuery(reinterpret_cast<void*>(Address), &Info, sizeof(Info)))
			return false;
		if (Info.State != MEM_COMMIT)
			return false;

		const auto Protect = Info.Protect & 0xff;
		return Protect == PAGE_EXECUTE ||
			Protect == PAGE_EXECUTE_READ ||
			Protect == PAGE_EXECUTE_READWRITE ||
			Protect == PAGE_EXECUTE_WRITECOPY;
	}

	LONG LogSehException(EXCEPTION_POINTERS* ExceptionInfo)
	{
		if (ExceptionInfo && ExceptionInfo->ExceptionRecord)
		{
			UE_LOG("SEH exception code=0x%08X address=%p",
				ExceptionInfo->ExceptionRecord->ExceptionCode,
				ExceptionInfo->ExceptionRecord->ExceptionAddress);
		}
		else
		{
			UE_LOG("SEH exception with no exception record");
		}
		return EXCEPTION_EXECUTE_HANDLER;
	}
}

static std::optional<GameInstance> TryGetGameInfo()
{
	wchar_t GameFilePath[MAX_PATH];
	GetModuleFileName(0, GameFilePath, MAX_PATH);

	DWORD verHandle = 0;
	DWORD verSize = GetFileVersionInfoSize(GameFilePath, &verHandle);

	if (!verSize)
		return std::nullopt;

	std::string VerData(verSize, '\0');
	uint32_t size = 0;
	VS_FIXEDFILEINFO* VersionInfo = nullptr;

	if (!GetFileVersionInfo(GameFilePath, verHandle, verSize, VerData.data()) ||
		!VerQueryValue(VerData.data(), L"\\", (VOID FAR * FAR*) & VersionInfo, &size) ||
		!size ||
		VersionInfo->dwSignature != 0xfeef04bd)
	{
		return std::nullopt;
	}

	auto VersionStr = std::format("{:d}.{:d}.{:d}.{:d}",
		(VersionInfo->dwFileVersionMS >> 16) & 0xffff,
		(VersionInfo->dwFileVersionMS >> 0) & 0xffff,
		(VersionInfo->dwFileVersionLS >> 16) & 0xffff,
		(VersionInfo->dwFileVersionLS >> 0) & 0xffff);

	GameInstance Ret;
	Ret.GamePath = std::filesystem::path(GameFilePath);
	Ret.Version = std::stof(VersionStr);

	return Ret;
}

static bool InitEngine(GameInstance& Game)
{
	// if an engine version has a different type or offset, set it here

	if (Game.GamePath.filename() == "FortniteClient-Win64-Shipping.exe"
		and Game.Version >= 5.0)
	{
		return IUnrealVersion::InitTypes<Version_FortniteLatest>();
	}

	return IUnrealVersion::InitTypes<UnrealVersionBase>();
}

bool App::Init()
{
	auto GameOpt = TryGetGameInfo();

	if (!GameOpt.has_value())
		return false;

	auto& Game = GameOpt.value();

	UE_LOG("Detected Unreal Engine version %f", Game.Version);
	UE_LOG("Detected game %s", Game.GamePath.filename().string().c_str());
	UE_LOG("Dumper mode %s", Research::ModeName());

	return InitEngine(Game);
}
