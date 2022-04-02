FROM mcr.microsoft.com/dotnet/runtime:6.0-alpine as run
WORKDIR /app
RUN apk add p7zip cpio git

FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine as build
WORKDIR /src
COPY ./UnityDataMiner/UnityDataMiner.csproj /src
RUN dotnet restore
COPY ./UnityDataMiner /src
RUN dotnet publish -c Release

FROM run
COPY --from=build /src/bin/Release/net6.0/publish /app
VOLUME [ "/data" ]
ENV LD_LIBRARY_PATH="${LD_LIBRARY_PATH}:/app/runtimes/alpine.3.9-x64/native"
CMD [ "dotnet", "UnityDataMiner.dll", "--repository", "/data", "--download-corlibs", "--download-libil2cpp-source" ]