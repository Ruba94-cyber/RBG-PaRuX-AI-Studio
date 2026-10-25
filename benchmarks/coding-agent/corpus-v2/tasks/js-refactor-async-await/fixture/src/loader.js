"use strict";
const DB = { 1: { name: "ann", planId: "p1" }, 2: { name: "bo", planId: "zz" } };
const PLANS = { p1: "pro" };

function fetchRaw(id, cb) {
  setImmediate(() => {
    if (!DB[id]) return cb(new Error("user not found: " + id));
    cb(null, DB[id]);
  });
}

function loadUser(id, cb) {
  fetchRaw(id, (err, raw) => {
    if (err) return cb(err);
    const plan = PLANS[raw.planId];
    if (!plan) return cb(new Error("unknown plan: " + raw.planId));
    cb(null, { id, name: raw.name, plan });
  });
}

module.exports = { fetchRaw, loadUser };
