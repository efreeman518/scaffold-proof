# TaskFlow.React

React + TypeScript reference UI for TaskFlow.

## Stack

- Vite SPA
- React Router
- TanStack Query
- MUI
- Typed API client for `/api/v1`

## Local

Run through Aspire when the full stack is needed. AppHost passes `VITE_API_BASE_URL` and Vite proxies `/api` to the gateway. Without that variable, the client uses relative `/api` requests only when the dev UI is served behind a same-origin gateway or reverse proxy. A standalone Vite server must set `VITE_API_BASE_URL` because it does not own the API route.

```powershell
dotnet run --project .\Host\Aspire\AppHost\AppHost.csproj
```

Standalone UI build:

```powershell
npm install
npm run build
```
