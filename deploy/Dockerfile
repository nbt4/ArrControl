FROM node:22-alpine AS web
WORKDIR /src
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY apps/web/package.json apps/web/
RUN pnpm install --frozen-lockfile
COPY docs/api/openapi.yaml docs/api/openapi.yaml
COPY apps/web apps/web
RUN pnpm --filter @arrcontrol/web build

FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS api
WORKDIR /src
COPY . .
RUN dotnet publish src/ArrControl.Api/ArrControl.Api.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
RUN apk add --no-cache tzdata \
    && addgroup -S arrcontrol \
    && adduser -S arrcontrol -G arrcontrol \
    && mkdir -p /var/lib/arrcontrol/data-protection-keys \
    && chown -R arrcontrol:arrcontrol /var/lib/arrcontrol \
    && chmod 0700 /var/lib/arrcontrol/data-protection-keys
COPY --from=api /out .
COPY --from=web /src/apps/web/dist ./wwwroot
USER arrcontrol
EXPOSE 8080
ENTRYPOINT ["dotnet", "ArrControl.Api.dll"]
