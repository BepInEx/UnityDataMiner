#!/bin/sh

docker run --rm -v "$(pwd)/tmp_data/repo:/data" -v "$(pwd)/tmp_data/config.toml:/app/config.toml" -it unity-miner