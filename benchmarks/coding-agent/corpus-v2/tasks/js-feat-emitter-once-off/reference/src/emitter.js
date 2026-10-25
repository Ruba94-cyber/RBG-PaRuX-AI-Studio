"use strict";
class Emitter {
  constructor() { this.listeners = new Map(); }
  on(event, listener) {
    if (!this.listeners.has(event)) this.listeners.set(event, []);
    this.listeners.get(event).push(listener);
    return this;
  }
  once(event, listener) {
    const wrapper = (...args) => {
      this.off(event, wrapper);
      listener(...args);
    };
    wrapper.original = listener;
    return this.on(event, wrapper);
  }
  off(event, listener) {
    const list = this.listeners.get(event);
    if (!list) return this;
    const i = list.findIndex((l) => l === listener || l.original === listener);
    if (i >= 0) list.splice(i, 1);
    return this;
  }
  emit(event, ...args) {
    const list = (this.listeners.get(event) || []).slice();
    for (const l of list) l(...args);
    return list.length > 0;
  }
}
module.exports = { Emitter };
