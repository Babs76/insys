/*
 * ============================================================================
 * OPENFACTOR TECHNOLOGY GROUP
 * CONFIDENTIAL AND PROPRIETARY SOFTWARE
 * ============================================================================
 * Copyright (c) 2026 OpenFactor Technology Group.
 * All rights reserved.
 * PRODUCT       : INSYS
 * COMPONENT     : Build
 * CONFIDENTIALITY AND INTELLECTUAL PROPERTY NOTICE
 * This source code and its associated documentation contain confidential and
 * proprietary information belonging to OpenFactor Technology Group or its
 * applicable intellectual property rights holder.
 * This material is provided solely for authorised development, maintenance,
 * operation or evaluation purposes, as applicable. No person may reproduce,
 * modify, distribute, disclose, publish or otherwise use this material except
 * as expressly permitted by an applicable written agreement or licence.
 * No ownership rights or licence to any intellectual property are granted
 * by the inclusion of this notice, except as expressly provided in an
 * applicable written agreement or licence.
 * Third-party software, open-source components and customer-owned materials
 * remain subject to their respective ownership rights and applicable licences
 * or agreements.
 * UNAUTHORISED ACCESS, USE, COPYING OR DISCLOSURE IS PROHIBITED.
 * ============================================================================
 */
// The Preview's login theme check (BR-168), in a real browser (Playwright's Chromium), through Keycloak's public address: the account console,
// whose sign-in uses the realm's login theme. As a temporary user made by theme-check-user.sh, it signs in with a password, sets up a one-time
// code on the two-factor setup page (the secret read from the page, the code computed here), reaches the account page, signs out, then signs
// in again with the password and a one-time code, and reaches the account page again. Every sign-in page must be served with the expected
// theme: a stylesheet under /resources/<version>/login/<theme>/. It prints PASS or FAIL per step only: never the password, the secret or a code.
//
//   node theme-check.cjs --url https://id.example.com --realm insys --username theme-check-1 --theme insys [--ignore-https-errors]
//
// The user's password comes on stdin. Exit 0 when every step passed, 1 otherwise. Needs the playwright package (NODE_PATH) and its Chromium.
'use strict';
const crypto = require('node:crypto');
const { chromium } = require('playwright');

function args(argv) {
  const out = { ignoreHttpsErrors: false };
  for (let i = 2; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--ignore-https-errors') out.ignoreHttpsErrors = true;
    else if (a.startsWith('--') && i + 1 < argv.length) out[a.slice(2)] = argv[++i];
    else throw new Error(`unknown argument ${a}`);
  }
  for (const k of ['url', 'realm', 'username', 'theme']) if (!out[k]) throw new Error(`--${k} is required`);
  if (!/^[A-Za-z0-9._-]+$/.test(out.theme)) throw new Error('--theme is not a theme name');
  out.url = out.url.replace(/\/+$/, '');
  return out;
}

async function readStdin() {
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  return Buffer.concat(chunks).toString('utf8').replace(/\r?\n$/, '');
}

// RFC 6238 (TOTP) over RFC 4648 base32, as Keycloak shows the secret (grouped by spaces).
function base32(text) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const ch of text.replace(/[\s=]/g, '').toUpperCase()) {
    const v = alphabet.indexOf(ch);
    if (v < 0) throw new Error('the secret is not base32');
    bits += v.toString(2).padStart(5, '0');
  }
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8) bytes.push(parseInt(bits.slice(i, i + 8), 2));
  return Buffer.from(bytes);
}
function totp(secret, policy, at = Date.now()) {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(at / 1000 / policy.period)));
  const mac = crypto.createHmac(policy.algorithm, base32(secret)).update(counter).digest();
  const offset = mac[mac.length - 1] & 0x0f;
  const value = (mac.readUInt32BE(offset) & 0x7fffffff) % 10 ** policy.digits;
  return value.toString().padStart(policy.digits, '0');
}

// The three results the run summary reports (Ade, 22 November 2026): sign-in (the first sign-in with the password), two-factor setup, and
// the one-time code (signing out, then in with the password and a code). A check not reached is a failure.
const groups = { 'sign-in': null, 'two-factor setup': null, 'one-time code': null };
function group(step) {
  if (step.startsWith('First sign-in')) return 'sign-in';
  if (step.startsWith('Two-factor setup')) return 'two-factor setup';
  return 'one-time code';
}
let failed = false;
function pass(step) { console.log(`PASS  ${step}`); const g = group(step); if (groups[g] === null) groups[g] = true; }
function fail(step, why) { failed = true; groups[group(step)] = false; console.log(`FAIL  ${step}${why ? `: ${why}` : ''}`); }
function results() {
  for (const [g, ok] of Object.entries(groups)) console.log(`RESULT  ${g}: ${ok ? 'PASS' : ok === false ? 'FAIL' : 'FAIL (not reached)'}`);
}
// In GitHub Actions, the two-factor secret and each code are masked before anything could print them; they are never printed anyway.
function mask(value) { if (process.env.GITHUB_ACTIONS === 'true' && value) console.log(`::add-mask::${value}`); }
// A step's page, for a failure: the path only, as a query can carry Keycloak's session codes.
function where(page) { try { return new URL(page.url()).pathname; } catch { return 'unknown'; } }

async function themeServed(page, theme, step) {
  const hrefs = await page.$$eval('link[rel="stylesheet"]', (links) => links.map((l) => l.href));
  const pattern = new RegExp(`/resources/[^/]+/login/${theme.replace(/\./g, '\\.')}/`);
  const served = hrefs.map((h) => { try { return new URL(h).pathname; } catch { return h; } });
  if (served.some((p) => pattern.test(p))) { pass(`${step}: served with the login theme ${theme}`); return true; }
  const themes = [...new Set(served.map((p) => (p.match(/\/login\/([^/]+)\//) || [])[1]).filter(Boolean))];
  fail(`${step}: served with the login theme ${theme}`, `its stylesheets are from ${themes.join(', ') || 'no login theme'}`);
  return false;
}

// Keycloak's own message on a refused form, if any: no input is echoed in it.
async function refusal(page) {
  const el = await page.$('#input-error, #input-error-otp-code, .pf-v5-c-alert__title, .kc-feedback-text');
  return el ? (await el.innerText()).trim().slice(0, 160) : '';
}

// The account console's own calls to the account API, with the bearer token it got from the sign-in: 200 once it reads the account, 401
// if it holds no valid token. On the INSYS realm it answers 403: the realm's client scopes (basic, offline_access, insys-api) give the
// account console's token no account roles, so the page shows its own error to every user, whatever the theme. Either 200 or 403 proves the
// sign-in finished and the console holds a token for this user; anything else, or no call, fails.
const accountApi = [];
function watchAccountApi(page, cfg) {
  page.on('response', (r) => {
    const req = r.request();
    let path = '';
    try { path = new URL(r.url()).pathname; } catch { return; }
    if (path.replace(/\/+$/, '') === `/realms/${cfg.realm}/account` && ['fetch', 'xhr'].includes(req.resourceType())) accountApi.push(r.status());
  });
}

async function accountPage(page, cfg, step) {
  const from = accountApi.length;
  try {
    await page.waitForURL((u) => u.pathname.startsWith(`/realms/${cfg.realm}/account`), { timeout: 60000 });
    await page.waitForSelector('#username, #kc-form-login, #otp', { state: 'detached', timeout: 5000 }).catch(() => {});
    for (let i = 0; i < 60 && accountApi.length === from; i++) await page.waitForTimeout(500);
  } catch {
    fail(`${step}: the account page loads`, `stopped at ${where(page)} ${await refusal(page)}`.trim());
    return false;
  }
  const got = accountApi.slice(from);
  if (got.includes(200)) { pass(`${step}: the account page loads, signed in (its account API answers 200)`); return true; }
  if (got.length > 0 && got.every((s) => s === 403)) {
    pass(`${step}: the account page loads, signed in (its account API answers 403: the realm gives the account console no account roles)`);
    return true;
  }
  fail(`${step}: the account page loads`, got.length ? `its account API answered ${[...new Set(got)].join(', ')}` : 'the account console made no call with a token');
  return false;
}

async function signInPage(page, cfg, step) {
  await page.goto(`${cfg.url}/realms/${cfg.realm}/account/`, { waitUntil: 'domcontentloaded', timeout: 60000 });
  try {
    await page.waitForSelector('#username', { timeout: 60000 });
    pass(`${step}: the account console sends to the sign-in page`);
  } catch {
    fail(`${step}: the account console sends to the sign-in page`, `stopped at ${where(page)}`);
    return false;
  }
  return themeServed(page, cfg.theme, `${step}: the sign-in page`);
}

async function password(page, cfg, secretText, step) {
  await page.fill('#username', cfg.username);
  await page.fill('#password', secretText);
  await page.click('#kc-login');
  await page.waitForLoadState('domcontentloaded');
}

async function main() {
  const cfg = args(process.argv);
  const userPassword = await readStdin();
  if (userPassword.length < 16) throw new Error("the user's password must come on stdin");
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({ ignoreHTTPSErrors: cfg.ignoreHttpsErrors });
    const page = await context.newPage();
    watchAccountApi(page, cfg);
    let secret = null;
    let policy = null;

    // 1. Sign-in, then the two-factor setup.
    if (!(await signInPage(page, cfg, 'First sign-in'))) return;
    await password(page, cfg, userPassword, 'First sign-in');
    try {
      await page.waitForSelector('#kc-totp-settings-form', { timeout: 60000 });
      pass('First sign-in: the password is taken, and the two-factor setup page follows');
    } catch {
      fail('First sign-in: the password is taken, and the two-factor setup page follows', `stopped at ${where(page)} ${await refusal(page)}`.trim());
      return;
    }
    await themeServed(page, cfg.theme, 'Two-factor setup: the page');
    if (!(await page.isVisible('#kc-totp-secret-key'))) {
      await page.click('#mode-manual');   // "Unable to scan?": the secret as text
      await page.waitForSelector('#kc-totp-secret-key', { timeout: 30000 });
    }
    const text = async (sel) => ((await page.$(sel)) ? (await page.innerText(sel)) : '');
    secret = (await text('#kc-totp-secret-key')).replace(/\s/g, '');
    const value = (s) => (s.split(':').pop() || '').trim();
    const algorithm = { SHA1: 'sha1', SHA256: 'sha256', SHA512: 'sha512', HmacSHA1: 'sha1', HmacSHA256: 'sha256', HmacSHA512: 'sha512' }[value(await text('#kc-totp-algorithm'))] || 'sha1';
    policy = { algorithm, digits: parseInt(value(await text('#kc-totp-digits')), 10) || 6, period: parseInt(value(await text('#kc-totp-period')), 10) || 30 };
    if (!/^[A-Z2-7]{16,}$/i.test(secret) || /counter/i.test(await text('#kc-totp-type'))) {
      fail('Two-factor setup: the secret is read from the page', 'no time-based secret shown');
      return;
    }
    mask(secret);
    pass('Two-factor setup: the secret is read from the page (not printed)');
    const first = totp(secret, policy);
    mask(first);
    await page.fill('#totp', first);
    if (await page.$('#userLabel')) await page.fill('#userLabel', 'theme check');
    const used = Math.floor(Date.now() / 1000 / policy.period);
    await page.click('#saveTOTPBtn');
    await page.waitForLoadState('domcontentloaded');
    if (!(await accountPage(page, cfg, 'Two-factor setup: the one-time code is taken, and'))) return;

    // 2. Sign out (the sign-out page is the login theme's too), then sign in with the password and a one-time code.
    await page.goto(`${cfg.url}/realms/${cfg.realm}/protocol/openid-connect/logout`, { waitUntil: 'domcontentloaded', timeout: 60000 });
    try {
      await page.waitForSelector('#kc-logout', { timeout: 30000 });
      await themeServed(page, cfg.theme, 'Sign-out: the confirmation page');
      await page.click('#kc-logout');
      await page.waitForLoadState('domcontentloaded');
      await page.waitForSelector('#kc-logout', { state: 'detached', timeout: 30000 });
      pass('Sign-out: signed out');
    } catch {
      fail('Sign-out: signed out', `stopped at ${where(page)}`);
      return;
    }
    if (!(await signInPage(page, cfg, 'Second sign-in'))) return;
    await password(page, cfg, userPassword, 'Second sign-in');
    try {
      await page.waitForSelector('#otp', { timeout: 60000 });
      pass('Second sign-in: the password is taken, and the one-time code page follows');
    } catch {
      fail('Second sign-in: the password is taken, and the one-time code page follows', `stopped at ${where(page)} ${await refusal(page)}`.trim());
      return;
    }
    await themeServed(page, cfg.theme, 'Second sign-in: the one-time code page');
    // A code is taken once: wait for the next period after the one used at setup.
    const wait = (used + 1) * policy.period * 1000 - Date.now() + 1500;
    if (wait > 0) await page.waitForTimeout(wait);
    const second = totp(secret, policy);
    mask(second);
    await page.fill('#otp', second);
    await page.click('#kc-login');
    await page.waitForLoadState('domcontentloaded');
    await accountPage(page, cfg, 'Second sign-in: the one-time code is taken, and');
  } finally {
    await browser.close();
  }
}

main().then(
  () => { results(); process.exit(failed || Object.values(groups).some((ok) => ok !== true) ? 1 : 0); },
  (e) => {
    failed = true;
    console.log(`FAIL  the check stopped: ${String((e && e.message) || e).split('\n')[0].slice(0, 200)}`);
    for (const g of Object.keys(groups)) if (groups[g] !== true) groups[g] = false;
    results();
    process.exit(1);
  },
);
