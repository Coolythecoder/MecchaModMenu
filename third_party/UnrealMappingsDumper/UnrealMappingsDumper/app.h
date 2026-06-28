#pragma once

namespace Research
{
	enum class DumperMode
	{
		Probe,
		ObjectScan,
		Dump
	};

	struct Config
	{
		DumperMode Mode = DumperMode::Probe;
		std::filesystem::path DllPath;
		std::filesystem::path DllDirectory;
		std::filesystem::path ConfigPath;
		std::filesystem::path LogPath;
		std::filesystem::path OutputPath;
	};

	void LoadConfig(HMODULE Module);
	const Config& GetConfig();
	const char* ModeName();
	void LogV(const char* str, va_list args);
	bool IsExecutableAddress(uintptr_t Address);
	LONG LogSehException(EXCEPTION_POINTERS* ExceptionInfo);
}

static void UE_LOG(const char* str, ...)
{
	va_list fmt;
	va_start(fmt, str);

	Research::LogV(str, fmt);

	va_end(fmt);
}

namespace App
{
	bool Init();
}
