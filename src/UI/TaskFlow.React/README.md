# TaskFlow.React

React + TypeScript reference UI for TaskFlow.

## Stack

- Vite SPA
- React Router
- TanStack Query
- MUI
- Typed API client for `/api/v1`

## Local

Run through Aspire when the full stack is needed. AppHost passes `VITE_API_BASE_URL` and Vite proxies `/api` to the gateway. Without that variable, the client uses relative `/api` requests on the current origin.

```powershell
dotnet run --project .\Host\Aspire\AppHost\AppHost.csproj
```

Standalone UI build:

```powershell
npm install
npm run build
```
