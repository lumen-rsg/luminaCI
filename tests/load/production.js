import http from 'k6/http';
import { check, sleep } from 'k6';

const baseUrl = (__ENV.BASE_URL || '').replace(/\/$/, '');
const username = __ENV.LOAD_USERNAME || '';
const password = __ENV.LOAD_PASSWORD || '';
const expectedVus = Number(__ENV.K6_VUS || '10');
const duration = __ENV.K6_DURATION || '2m';
const p95Milliseconds = Number(__ENV.K6_P95_MILLISECONDS || '1000');
const p99Milliseconds = Number(__ENV.K6_P99_MILLISECONDS || '2000');

export const options = {
  scenarios: {
    authenticated_console: {
      executor: 'constant-vus',
      vus: expectedVus,
      duration,
      gracefulStop: '30s',
    },
  },
  thresholds: {
    checks: ['rate>0.995'],
    http_req_failed: ['rate<0.005'],
    http_req_duration: [
      `p(95)<${p95Milliseconds}`,
      `p(99)<${p99Milliseconds}`,
    ],
  },
};

let authenticated = false;
let accessCookie = '';

function authenticate() {
  const response = http.post(
    `${baseUrl}/api/auth/login`,
    JSON.stringify({ username, password }),
    {
      headers: { 'Content-Type': 'application/json' },
      tags: { name: 'POST /api/auth/login' },
    },
  );
  const accessCookies = response.cookies.lumina_access || [];
  accessCookie = accessCookies.length > 0
    ? `lumina_access=${accessCookies[0].value}`
    : '';
  authenticated = check(response, {
    'login succeeds and returns an access cookie': (result) =>
      result.status === 200 && accessCookie.length > 0,
  });
}

function authenticatedRequest(name) {
  return {
    headers: { Cookie: accessCookie },
    tags: { name },
  };
}

export function setup() {
  if (!baseUrl || !username || !password) {
    throw new Error('BASE_URL, LOAD_USERNAME, and LOAD_PASSWORD are required');
  }
}

export default function () {
  if (!authenticated) {
    authenticate();
    if (!authenticated) {
      sleep(1);
      return;
    }
  }

  const responses = http.batch([
    ['GET', `${baseUrl}/`, null, { tags: { name: 'GET /' } }],
    ['GET', `${baseUrl}/health/ready`, null, { tags: { name: 'GET /health/ready' } }],
    ['GET', `${baseUrl}/api/pipelines?page=1&pageSize=20`, null, authenticatedRequest('GET /api/pipelines')],
    ['GET', `${baseUrl}/api/builds?page=1&pageSize=20`, null, authenticatedRequest('GET /api/builds')],
    ['GET', `${baseUrl}/api/repository?page=1&pageSize=20`, null, authenticatedRequest('GET /api/repository')],
    ['GET', `${baseUrl}/api/sources?page=1&pageSize=20`, null, authenticatedRequest('GET /api/sources')],
  ]);

  check(responses[0], { 'console responds': (response) => response.status === 200 });
  check(responses[1], {
    'dependencies remain healthy': (response) =>
      response.status === 200 && response.json('status') === 'Healthy',
  });
  check(responses[2], { 'pipelines respond': (response) => response.status === 200 });
  check(responses[3], { 'builds respond': (response) => response.status === 200 });
  check(responses[4], { 'repositories respond': (response) => response.status === 200 });
  check(responses[5], { 'sources respond': (response) => response.status === 200 });

  sleep(1);
}
