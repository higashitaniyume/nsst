import type {
  ConfigResponse,
  HealthResponse,
  InfoResponse,
  MetricsResponse,
  Protocol,
  StreamParams,
} from './types.js';

/** Builds a URL relative to the origin serving the console. */
export function relativeURL(path: string): string {
  return new URL(path, window.location.origin).toString();
}

async function getJSON<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    cache: 'no-store',
    signal,
  });
  if (!response.ok) {
    throw new Error(`${path} responded with HTTP ${response.status}`);
  }
  return (await response.json()) as T;
}

export function fetchHealth(signal?: AbortSignal): Promise<HealthResponse> {
  return getJSON<HealthResponse>('/api/health', signal);
}

export function fetchInfo(signal?: AbortSignal): Promise<InfoResponse> {
  return getJSON<InfoResponse>('/api/info', signal);
}

export function fetchConfig(signal?: AbortSignal): Promise<ConfigResponse> {
  return getJSON<ConfigResponse>('/api/config', signal);
}

export function fetchMetrics(signal?: AbortSignal): Promise<MetricsResponse> {
  return getJSON<MetricsResponse>('/api/metrics', signal);
}

/** Path of the stream endpoint for a protocol. */
export function streamPath(protocol: Protocol): string {
  switch (protocol) {
    case 'http-stream':
      return '/api/stream/http';
    case 'sse':
      return '/api/stream/sse';
    case 'websocket':
      return '/api/stream/ws';
  }
}

function query(params: StreamParams): string {
  const search = new URLSearchParams({
    duration: String(params.duration),
    interval: String(params.interval),
    payload_size: String(params.payload_size),
  });
  return search.toString();
}

/**
 * Absolute HTTP(S) URL for the streaming endpoints.
 *
 * Requests are always same-origin, which is why the console works with the
 * server's default CORS policy of "same origin only".
 */
export function httpStreamURL(protocol: Exclude<Protocol, 'websocket'>, params: StreamParams): string {
  return relativeURL(`${streamPath(protocol)}?${query(params)}`);
}

/** Absolute ws(s) URL for the WebSocket endpoint. */
export function websocketURL(params: StreamParams): string {
  const url = new URL(streamPath('websocket'), window.location.origin);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.search = query(params);
  return url.toString();
}
