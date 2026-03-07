export function createRateLimiter(maxRps) {
  let queue = Promise.resolve();
  return (fn) => {
    const slot = queue.then(() =>
      new Promise(resolve => setTimeout(resolve, 1000 / maxRps))
    ).then(fn);
    // Recover queue on rejection so subsequent calls aren't poisoned
    queue = slot.catch(() => {});
    return slot;
  };
}
