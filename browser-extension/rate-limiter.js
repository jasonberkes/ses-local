export function createRateLimiter(maxRps) {
  let queue = Promise.resolve();
  return (fn) => {
    queue = queue.then(() =>
      new Promise(resolve => setTimeout(resolve, 1000 / maxRps))
    ).then(fn);
    return queue;
  };
}
