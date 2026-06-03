FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["IECC/IECC.csproj", "IECC/"]
RUN dotnet restore "IECC/IECC.csproj"
COPY . .
WORKDIR "/src/IECC"
RUN dotnet publish "IECC.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IECC.dll"]
