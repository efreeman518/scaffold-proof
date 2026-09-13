export type RuntimeConfig = Readonly<{ gatewayBaseUrl: string }>

let runtimeConfig: RuntimeConfig | undefined

/** Loads the deployment-supplied gateway origin before production requests begin. */
export async function loadRuntimeConfig(fetcher: typeof fetch = fetch): Promise<RuntimeConfig> {
  if (import.meta.env.DEV) {
    runtimeConfig = { gatewayBaseUrl: normalizeGatewayBaseUrl(import.meta.env.VITE_API_BASE_URL ?? '') }
    return runtimeConfig
  }

  const response = await fetcher('/app-config.json', { cache: 'no-store' })
  if (!response.ok) {
    throw new Error(`Runtime configuration request failed with ${response.status}.`)
  }

  let config: unknown
  try {
    config = await response.json()
  } catch {
    throw new Error('Runtime configuration is not valid JSON.')
  }

  if (!config || typeof config !== 'object' || !('gatewayBaseUrl' in config)) {
    throw new Error('Runtime configuration must define gatewayBaseUrl.')
  }

  runtimeConfig = { gatewayBaseUrl: normalizeGatewayBaseUrl(config.gatewayBaseUrl) }
  return runtimeConfig
}

/** Returns the gateway origin only after startup validated the runtime configuration. */
export function gatewayBaseUrl(): string {
  if (!runtimeConfig) {
    throw new Error('Runtime configuration has not loaded.')
  }

  return runtimeConfig.gatewayBaseUrl
}

export function normalizeGatewayBaseUrl(value: unknown): string {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error('Runtime configuration gatewayBaseUrl must be a non-empty HTTP(S) URL.')
  }

  let parsed: URL
  try {
    parsed = new URL(value)
  } catch {
    throw new Error('Runtime configuration gatewayBaseUrl must be a valid HTTP(S) URL.')
  }

  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
    throw new Error('Runtime configuration gatewayBaseUrl must use HTTP or HTTPS.')
  }

  return parsed.toString().replace(/\/$/, '')
}
