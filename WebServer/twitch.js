const tmi = require('tmi.js');

function startTwitch(config, emit) {
  const client = new tmi.Client({
    identity: {
      username: config.username,
      password: config.oauth
    },
    channels: [config.channel]
  });

  client.on('message', (channel, tags, message, self) => {
    if (self) return;

    emit({
      platform: 'twitch',
      event: 'chat',
      user: {
        id: tags['user-id'],
        name: tags['display-name'] || tags.username,
        roles: [
          tags.mod && 'moderator',
          tags.subscriber && 'subscriber'
        ].filter(Boolean)
      },
      data: {
        message
      },
      timestamp: Date.now()
    });
  });

  client.connect();
}

module.exports = { startTwitch };
