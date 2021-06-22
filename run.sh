#!/bin/sh

docker run --rm -v "$(pwd)/data/repo:/data" -v "$(pwd)/data/config.toml:/app/config.toml" -it unity-miner