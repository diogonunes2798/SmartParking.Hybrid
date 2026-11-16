// Smoke script of the Phase 9a measurement chain: UC-03 reads (GET /api/availability) at a constant, low arrival rate.
// It proves that k6 → summary JSON → CSV works; it is not the availability-read scenario (rising rate, RNF-01), which
// comes in its own request. Run by bench/run.sh through `docker compose --profile bench run --rm k6`.
//
// Environment (-e): API (base URL), RATE (requests/s), DURATION (e.g. 30s), PREFIX (facility name prefix), SEED.
import http from 'k6/http';
import { check } from 'k6';

const API = __ENV.API || 'http://host.docker.internal:5080';
const PREFIX = __ENV.PREFIX || 'sim-d8-';

export const options = {
  discardResponseBodies: true,
  // Always-true thresholds: k6 exports a tagged sub-metric only when a threshold names it. They separate the measured
  // requests (name:availability) from the setup() requests, which the global http_req_* metrics also count.
  thresholds: {
    'http_req_duration{name:availability}': ['max>=0'],
    'http_req_failed{name:availability}': ['rate>=0'],
    'http_reqs{name:availability}': ['count>=0'],
  },
  scenarios: {
    reads: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RATE || 20),
      timeUnit: '1s',
      duration: __ENV.DURATION || '30s',
      preAllocatedVUs: 10,
      maxVUs: 50,
    },
  },
};

// Points of the seeded facilities (the simulator provisions them in bench/run.sh, step 3). Pages of 200 (MaxTake).
export function setup() {
  const points = [];
  for (let skip = 0; ; skip += 200) {
    const res = http.get(`${API}/api/facilities?skip=${skip}&take=200`, { responseType: 'text', tags: { name: 'setup' } });
    if (res.status !== 200) {
      throw new Error(`GET /api/facilities returned ${res.status}`);
    }
    const page = res.json();
    for (const f of page) {
      if (f.name.startsWith(PREFIX)) {
        points.push({ lat: f.location.coordinates[1], lon: f.location.coordinates[0] }); // GeoJSON: lon, lat
      }
    }
    if (page.length < 200) {
      break;
    }
  }
  if (points.length === 0) {
    throw new Error(`no facility named ${PREFIX}*: was the base data loaded?`);
  }
  return { points };
}

// Deterministic choice of the point (iteration number), so that two runs send the same sequence of queries.
export default function (data) {
  const p = data.points[(__ITER * 7 + __VU) % data.points.length];
  const res = http.get(`${API}/api/availability?lat=${p.lat}&lon=${p.lon}&radius=500&limit=20`, { tags: { name: 'availability' } });
  check(res, { 'status 200': (r) => r.status === 200 });
}
