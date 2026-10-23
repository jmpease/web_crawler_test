# One image recipe for both services; PROJECT picks which one (Crawler.Api or Crawler.Worker).
ARG PROJECT=Crawler.Api

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /src

# Restore first, from project files only, so source edits don't invalidate the package layer.
COPY global.json Directory.Build.props .editorconfig ./
COPY src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY src/Crawler.Infrastructure/Crawler.Infrastructure.csproj src/Crawler.Infrastructure/
COPY src/${PROJECT}/${PROJECT}.csproj src/${PROJECT}/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj

COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
# The .NET 8 images listen on 8080 and ship a non-root "app" user.
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
