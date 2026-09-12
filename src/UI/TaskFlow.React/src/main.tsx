import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { AppProviders } from './app/AppProviders'
import { loadRuntimeConfig } from './api/runtimeConfig'
import './index.css'

async function bootstrap() {
  await loadRuntimeConfig()

  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <AppProviders>
        <App />
      </AppProviders>
    </StrictMode>,
  )
}

void bootstrap().catch((error: unknown) => {
  console.error(error)
  document.getElementById('root')!.textContent = 'TaskFlow could not load its gateway configuration.'
})
