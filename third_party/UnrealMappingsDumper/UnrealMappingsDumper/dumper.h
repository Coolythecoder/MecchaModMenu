#pragma once

#include "unrealTypes.h"

namespace Dumper
{
	void ScanObjects();
	void Run(ECompressionMethod CompressionMethod, const char* OutputPath);
};
