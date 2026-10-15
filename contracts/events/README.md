# Realtime events (SignalR)

HTTP is described by `openapi/ovrilo-api.json`; realtime events are described here (CONTRACTS.md §2, ADR-0007).

## Connection

- Hub: `/hubs/realtime` (SignalR, JSON protocol, camelCase payloads, enums as camelCase strings).
- Authentication: the same access token as REST. HTTP transports send `Authorization: Bearer …`; WebSockets pass it as the `access_token` query parameter (accepted only on `/hubs/*`). The server closes the connection when the access token expires (about every 15 minutes); reconnect with a fresh token and catch up.
- Delivery is best effort. The database is the source of truth: after (re)connecting, re-subscribe and fetch what you missed with `GET /channels/{channelId}/messages?after=<last seen id>`. Handlers must be idempotent (the same message can arrive by event and by REST).

## Client → server

| Method | Arguments | Returns | Notes |
| --- | --- | --- | --- |
| `SubscribeChannel` | `channelId: uuid` | `boolean` | `false` when the caller cannot read the channel; nothing is subscribed. Subscriptions last for the connection. |
| `UnsubscribeChannel` | `channelId: uuid` | — | Idempotent. |
| `SubscribeCommunity` | `communityId: uuid` | `{ onlineUserIds: uuid[] } \| null` | Presence of the community's members: who is online now, then `PresenceChanged` events. `null` when the caller is not an active member; nothing is subscribed. |
| `UnsubscribeCommunity` | `communityId: uuid` | — | Idempotent. |

## Server → client

| Event | Payload | Sent when |
| --- | --- | --- |
| `MessageCreated` | `MessageResponse` | A new message was stored in a subscribed channel. Not sent for idempotent replays of the same `clientMessageId`. |
| `MessageEdited` | `MessageResponse` | Its author changed the content (`editedAt` set). |
| `MessageDeleted` | `MessageResponse` (tombstone: `content: null`, `deletedAt` set) | The message was deleted. |

| `ReadStateUpdated` | `ReadStateResponse` (`{ channelId, lastReadMessageId }`) | Sent to the reader's own connections when `PUT /channels/{channelId}/read-state` moved their position forward, so other devices clear the unread badge. No channel subscription needed. |
| `PresenceChanged` | `{ userId: uuid, online: boolean }` | A member of a subscribed community came online (first open connection) or went offline (last connection closed, after a grace period of `Realtime:PresenceGraceSeconds`, default 10, so reconnects do not flicker). |
| `UserUpdated` | `{ userId: uuid, displayName: string, avatarAssetId: uuid \| null }` | A member of a subscribed community (or the caller, on their other devices) changed their avatar. Refetch the avatar by its new asset ID; `null` means initials. |
| `CommunityUpdated` | `{ communityId: uuid, name: string, description: string \| null, iconAssetId: uuid \| null }` | A subscribed community's name, description or icon changed. |
| `CommunityDeleted` | `{ communityId: uuid }` | The owner deleted a subscribed community. Remove it from the list, leave its voice channel if connected; its REST endpoints now answer `404`. |
| `ChannelsChanged` | `{ communityId: uuid, channelId: uuid, change: "created" \| "updated" \| "deleted" }` | A channel of a subscribed community was created, renamed, re-topiced, moved or deleted. Refetch `GET /communities/{communityId}/channels`: a move renumbers the other channels of its type, so do not patch the list from the payload. Clients showing a deleted channel move away from it, and clients connected to a deleted voice channel leave it (the server does not eject them yet). |

`MessageResponse` is the OpenAPI schema of the same name.

Asset IDs never change content: a new avatar or icon gets a new ID, so clients can cache image bytes by asset ID and fetch short-lived URLs with `POST /media/download-urls`.

Presence is global per user: a `PresenceChanged` reaches every community the user belongs to, and clients may keep one user → online map. Members list and roles come from `GET /communities/{communityId}/members`.

## Compatibility

Additive changes only within a contract version: new events and new payload fields may appear, and clients ignore what they do not know. Removing or renaming an event or field is a breaking change and requires deploying consumers first (CONTRACTS.md §2).

Known limitations:

- Presence is tracked in memory by the API process, which is correct for the single instance deployed today. Running several API instances requires a shared store (for example a Redis backplane and a shared presence set).
- Someone who joins a community after connecting announces their presence there only on their next connection; others see them after re-subscribing.
- A subscription is checked when it is made. If someone loses access to a channel (left or removed from the community), they keep receiving its events until they reconnect.
