const WebSocket = require('ws');

const clients = new Set();

function startWSServer(port) {
  const wss = new WebSocket.Server({ port });

  wss.on('connection', ws => {
    console.log('[WS] Terraria connected');
    clients.add(ws);

    ws.on('close', () => {
      clients.delete(ws);
      console.log('[WS] Terraria disconnected');
    });
  });

  return {
    broadcast(data) {
      const json = JSON.stringify(data);
      for (const ws of clients) {
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(json);
        }
      }
    }
  };
}

module.exports = { startWSServer };
