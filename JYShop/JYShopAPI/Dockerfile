FROM microsoft/dotnet:2.2-aspnetcore-runtime AS base
WORKDIR /app
EXPOSE 5012

FROM microsoft/dotnet:2.2-sdk AS build
WORKDIR /src
COPY ["JYShopAPI/JYShopAPI.csproj", "JYShopAPI/"]
RUN dotnet restore "JYShopAPI/JYShopAPI.csproj"
COPY . .
WORKDIR "/src/JYShopAPI"
RUN dotnet build "JYShopAPI.csproj" -c Release -o /app

FROM build AS publish
RUN dotnet publish "JYShopAPI.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "JYShopAPI.dll"]
