"use strict";
const DB = { 1: { name: "ann", planId: "p1" }, 2: { name: "bo", planId: "zz" } };
const PLANS = { p1: "pro" };

function fetchRaw(id, cb) {
  setImmediate(() => {
    if (!DB[id]) return cb(new Error("user not found: " + id));
    cb(null, DB[id]);
  });
}

function fetchRawAsync(id) {
  return new Promise((resolve, reject) => {
    fetchRaw(id, (err, raw) => (err ? reject(err) : resolve(raw)));
  });
}

async function loadUser(id) {
  const raw = await fetchRawAsync(id);
  const plan = PLANS[raw.planId];
  if (!plan) throw new Error("unknown plan: " + raw.planId);
  return { id, name: raw.name, plan };
}

module.exports = { fetchRaw, loadUser };
