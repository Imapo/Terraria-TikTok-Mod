const { google } = require('googleapis');

async function startYouTube(config, emit) {
  const youtube = google.youtube({
    version: 'v3',
    auth: config.apiKey
  });

  async function poll() {
    const res = await youtube.liveChatMessages.list({
      liveChatId: config.liveChatId,
      part: 'snippet,authorDetails'
    });

    for (const item of res.data.items) {
      emit({
        platform: 'youtube',
        event: 'chat',
        user: {
          id: item.authorDetails.channelId,
          name: item.authorDetails.displayName,
          roles: [
            item.authorDetails.isChatModerator && 'moderator',
            item.authorDetails.isChatSponsor && 'member'
          ].filter(Boolean)
        },
        data: {
          message: item.snippet.displayMessage
        },
        timestamp: Date.now()
      });
    }

    setTimeout(poll, 3000);
  }

  poll();
}

module.exports = { startYouTube };
