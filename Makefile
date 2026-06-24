GAME_ROOT ?= C:\Program Files (x86)\Steam\steamapps\common\MECCHA CHAMELEON
NATIVE_APPLY_MODE ?= static_hybrid_front_side_probe
DEV_PROBE_ARGS ?= --auto-sdk-probe --auto-sdk-deep-probe
VERSION ?= 1.0.0
DEV_FLOW_PS := scripts/dev_flow.ps1
PACKAGE_PS := scripts/package_release.ps1

.PHONY: build run probe copy-to-game package clean

define RUN_DEV_FLOW
	@if command -v pwsh >/dev/null 2>&1; then \
		pwsh -NoProfile -ExecutionPolicy Bypass -File $(DEV_FLOW_PS) $(1); \
	elif command -v powershell.exe >/dev/null 2>&1; then \
		PS_SCRIPT_WIN="$$(if command -v wslpath >/dev/null 2>&1; then wslpath -w $(DEV_FLOW_PS); else printf '%s' $(DEV_FLOW_PS); fi)"; \
		powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$$PS_SCRIPT_WIN" $(1); \
	else \
		echo "PowerShell runtime not found." >&2; exit 127; \
	fi
endef

build:
	$(call RUN_DEV_FLOW,-Action build)

run: build
	$(call RUN_DEV_FLOW,-Action run -RuntimeArgString "--mode service --native-apply-mode $(NATIVE_APPLY_MODE) $(DEV_PROBE_ARGS)")

probe: build
	$(call RUN_DEV_FLOW,-Action run -RuntimeArgString "--mode probe")

copy-to-game: build
	$(call RUN_DEV_FLOW,-Action deploy -GameRoot '$(GAME_ROOT)')

package: build
	@if command -v pwsh >/dev/null 2>&1; then \
		pwsh -NoProfile -ExecutionPolicy Bypass -File $(PACKAGE_PS) -Version $(VERSION); \
	elif command -v powershell.exe >/dev/null 2>&1; then \
		PS_SCRIPT_WIN="$$(if command -v wslpath >/dev/null 2>&1; then wslpath -w $(PACKAGE_PS); else printf '%s' $(PACKAGE_PS); fi)"; \
		powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$$PS_SCRIPT_WIN" -Version $(VERSION); \
	else \
		echo "PowerShell runtime not found." >&2; exit 127; \
	fi

clean:
	rm -rf .build
