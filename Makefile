BIN_DIR  := $(HOME)/.local/bin
BIN      := $(BIN_DIR)/git-cp
PUBLISH  := bin/publish/linux/git-cp

.PHONY: build install

build:
	dotnet publish -c Release -r linux-x64 --self-contained true \
		-p:PublishSingleFile=true -o bin/publish/linux

install: build
	@mkdir -p $(BIN_DIR)
	cp $(PUBLISH) $(BIN)
	@echo "Installed → $(BIN)"
