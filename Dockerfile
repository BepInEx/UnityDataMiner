FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine as run
WORKDIR /app
RUN apk add 7zip cpio git elfutils

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine as build
WORKDIR /src
COPY ./UnityDataMiner/UnityDataMiner.csproj /src
RUN dotnet restore
COPY ./UnityDataMiner /src
RUN dotnet publish -c Release

FROM run
COPY --from=build /src/bin/Release/net8.0/publish /app
VOLUME [ "/data" ]
ENV LD_LIBRARY_PATH="${LD_LIBRARY_PATH}:/app/runtimes/alpine.3.9-x64/native"
RUN git config --global --add safe.directory /data
CMD [ "dotnet", "UnityDataMiner.dll", "--repository", "/data" ]