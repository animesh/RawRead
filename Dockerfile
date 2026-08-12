FROM mcr.microsoft.com/dotnet/sdk:8.0

# Install git
RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Clone repository into current working directory
RUN git clone https://github.com/animesh/RawRead .

# Build release binaries
RUN dotnet build -c Release

# Run the raw reader binary against target raw file
RUN dotnet bin/Release/net8.0/RawRead.dll 171010_Ip_Hela_ugi.raw

CMD ["sleep", "inf"]