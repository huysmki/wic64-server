# WiC64 browser: C64 program + .NET server
#
#   make                          build the C64 programs (build/) and the sample program
#   make server                   run the .NET server on port $(PORT)
#   make push PRG=file.prg        run file.prg on the C64 (the browser menu must be on screen)
#   make push PRG=file.prg SAVE=1 same, but save it to disk (device 8) first
#   make vice                     start VICE (3.8 or later) with the WiC64 emulation and the browser
#
# The C64 program proposes the address it saved on disk (file "WIC64 SERVER" on
# device 8) the last time. Without that file it proposes this built-in address:
#   make                               the generic "mypc:6464"
#   make SERVER_HOST=mymac.lan         a name your router's DNS knows, or an IP address
#   make BONJOUR=1                     this computer's mDNS name (e.g. Kims-iMac.local)
#   make IP=1                          this computer's current IP address (macOS and Linux)

ifeq ($(BONJOUR),1)
SERVER_HOST ?= $(shell (scutil --get LocalHostName 2>/dev/null || hostname -s)).local
endif
ifeq ($(IP),1)
SERVER_HOST ?= $(shell ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || hostname -I 2>/dev/null | cut -d' ' -f1)
endif
SERVER_HOST ?= mypc
PORT        ?= 6464
SERVER_ADDRESS = $(SERVER_HOST):$(PORT)

ACME      ?= acme
ACMEFLAGS  = -v1 -I c64 -I c64/wic64-library -I build
VICE      ?= x64sc
# USERPORT_DEVICE_WIC64 in VICE's userport.h
VICEFLAGS ?= -userportdevice 23

PRG       ?= content/prg/hello.prg
SAVE      ?= 0

.PHONY: all server push vice clean FORCE

all: build/browser.prg build/standalone.bin build/loadhelper.bin content/prg/hello.prg content/prg/loadtest.prg

build/browser.prg: c64/browser.asm c64/wic64-library/wic64.asm c64/wic64-library/wic64.h build/config.asm
	$(ACME) $(ACMEFLAGS) -f cbm -l build/browser.sym -o $@ c64/browser.asm
	@echo "Built $@ with proposed server address $(SERVER_ADDRESS)"

# The server sends this player to the C64 for tunes that can not play in the background
build/standalone.bin: c64/standalone.asm
	@mkdir -p build
	$(ACME) $(ACMEFLAGS) -f plain -o $@ $<

# The server sends this helper with programs, so they can LOAD more files from the server
build/loadhelper.bin: c64/loadhelper.asm
	@mkdir -p build
	$(ACME) $(ACMEFLAGS) -f plain -o $@ $<

content/prg/%.prg: c64/samples/%.asm
	$(ACME) -f cbm -o $@ $<

# Only rewritten when the URL changes, so the program is rebuilt when you switch networks
build/config.asm: FORCE
	@mkdir -p build
	@test -n "$(SERVER_HOST)" || (echo "Could not detect this computer's address, use make SERVER_HOST=..." && false)
	@test $$(printf '%s' "$(SERVER_ADDRESS)" | wc -c) -le 30 || (echo "Server address $(SERVER_ADDRESS) is longer than the 30 characters the C64 can hold" && false)
	@config=$$(printf '!macro server_address_text {\n    !text "%s"\n}' "$(SERVER_ADDRESS)"); \
	 [ "$$(cat $@ 2>/dev/null)" = "$$config" ] || echo "$$config" > $@

# Folders, port and more are set in server/appsettings.json; PORT=... on the make command line overrides the port
server: all
	dotnet run --project server $(if $(filter command line,$(origin PORT)),-- --Port=$(PORT))

push:
	@test -f "$(PRG)" || (echo "No such file: $(PRG)" && false)
	@curl -sS --fail-with-body --data-binary @"$(PRG)" -H "Content-Type: application/octet-stream" \
		"http://localhost:$(PORT)/push?save=$(SAVE)&name=$(notdir $(PRG))"

vice: build/browser.prg
	$(VICE) $(VICEFLAGS) -autostart build/browser.prg

clean:
	rm -rf build server/bin server/obj
