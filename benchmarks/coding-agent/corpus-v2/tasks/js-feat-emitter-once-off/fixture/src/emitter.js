"use strict";
class Emitter {
  constructor() { this.listeners = new Map(); }
  on(event, listener) {
    if (!this.listeners.has(event)) this.listeners.set(event, []);
    this.listeners.get(event).push(listener);
    return this;
  }
  emit(event, ...args) {
    const list = this.listeners.get(event) || [];
    for (const l of list) l(...args);
  }
}
module.exports = { Emitter };
