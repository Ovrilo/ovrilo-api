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
| `SubscribeCommunity` | `communityId: uuid` | `{ onlineUserIds: uuid[], statuses: { userId, status }[] } \| null` | Presence of the community's members: who is online now and their status, then `PresenceChanged` events. `null` when the caller is not an active member; nothing is subscribed. |
| `UnsubscribeCommunity` | `communityId: uuid` | — | Idempotent. |
| `SetIdle` | `idle: boolean` | — | This device's computer had no keyboard or mouse input for a while (`true`) or is back (`false`). The user shows as `away` while every connected device is idle. Not stored. |
| `GetFriendsPresence` | — | `{ onlineUserIds: uuid[], statuses: { userId, status }[] }` | Friends online now. Friends' later changes arrive as `PresenceChanged` without subscribing; call again after reconnecting and after `FriendsChanged`. |

## Server → client

| Event | Payload | Sent when |
| --- | --- | --- |
| `MessageCreated` | `MessageResponse` | A new message was stored in a subscribed channel, or in one of your direct or group conversations (sent to every member's own connections without subscribing; `channelId` is the conversation id). Not sent for idempotent replays of the same `clientMessageId`. |
| `MessageEdited` | `MessageResponse` | Its author changed the content (`editedAt` set). |
| `MessageDeleted` | `MessageResponse` (tombstone: `content: null`, `deletedAt` set) | The message was deleted. |

| `ReadStateUpdated` | `ReadStateResponse` (`{ channelId, lastReadMessageId }`) | Sent to the reader's own connections when `PUT /channels/{channelId}/read-state` moved their position forward, so other devices clear the unread badge. No channel subscription needed. |
| `FriendsChanged` | `{ change: "requestReceived" \| "friendAdded" \| "updated", user: SocialUserResponse }` | Sent to the user's own connections when their friends, requests or blocks changed; `user` is the other person. Clients refetch `GET /friends` and `GET /friends/requests` (and `/blocks`). No subscription needed. |
| `CallRinging` | `{ conversationId, caller: { id, displayName, avatarAssetId }, isGroup: boolean, groupName: string \| null }` | Someone started a call in one of your direct or group conversations (`POST /dms/{id}/call`; groups use the same endpoints with the group id). Answer with `POST /dms/{id}/call/join`, decline with `/decline`. Sent to each callee's own connections. |
| `CallAccepted` | `{ conversationId }` | Someone answered. Sent to the caller's own connections on the first answer, and to the answering person's own connections (their other devices stop ringing). |
| `CallEnded` | `{ conversationId, reason: "declined" \| "cancelled" \| "missed" }` | A ringing call stopped without being answered. `declined`: sent to the person who declined; the caller gets it once everyone they rang declined and nobody answered. `cancelled`: the caller hung up first; sent to the caller and everyone still ringing. `missed`: not answered within `VoiceSessions:Calls:RingSeconds` (default 30); sent to everyone still ringing, and to the caller when nobody answered. A connected call simply ends when people leave the SFU room. |
| `GroupChanged` | `{ change: "added" \| "updated" \| "removed", groupId: uuid }` | Sent to members' own connections when a group was created, renamed, gained or lost members, or changed owner. `added`: you are now a member; `removed`: you left or were removed. Clients refetch `GET /groups`. |
| `PresenceChanged` | `{ userId: uuid, online: boolean, status: "online" \| "away" \| "doNotDisturb" \| null }` | What a member of a subscribed community, or a friend, shows changed: came online (first open connection), went offline (last connection closed, after a grace period of `Realtime:PresenceGraceSeconds`, default 10, so reconnects do not flicker), or changed status. `status` is null when offline. The status comes from the user's choice (`PUT /users/me/status`) and idle devices (`SetIdle`); a user who chose `invisible` looks offline. |
| `UserUpdated` | `{ userId: uuid, displayName: string, avatarAssetId: uuid \| null }` | A member of a subscribed community (or the caller, on their other devices) changed their avatar. Refetch the avatar by its new asset ID; `null` means initials. |
| `CommunityUpdated` | `{ communityId: uuid, name: string, description: string \| null, iconAssetId: uuid \| null }` | A subscribed community's name, description or icon changed. |
| `CommunityDeleted` | `{ communityId: uuid }` | The owner deleted a subscribed community. Remove it from the list, leave its voice channel if connected; its REST endpoints now answer `404`. |
| `ChannelsChanged` | `{ communityId: uuid, channelId: uuid, change: "created" \| "updated" \| "deleted" }` | A channel of a subscribed community was created, renamed, re-topiced, moved or deleted. Refetch `GET /communities/{communityId}/channels`: a move renumbers the other channels of its type, so do not patch the list from the payload. Clients showing a deleted channel move away from it, and clients connected to a deleted voice channel leave it (the server does not eject them yet). |

`MessageResponse` is the OpenAPI schema of the same name. `MessageEdited` and `MessageDeleted` for direct and group conversations also go to every member's own connections.

Asset IDs never change content: a new avatar or icon gets a new ID, so clients can cache image bytes by asset ID and fetch short-lived URLs with `POST /media/download-urls`.

Presence is global per user: a `PresenceChanged` reaches every community the user belongs to, and clients may keep one user → online map. Members list and roles come from `GET /communities/{communityId}/members`.

## Compatibility

Additive changes only within a contract version: new events and new payload fields may appear, and clients ignore what they do not know. Removing or renaming an event or field is a breaking change and requires deploying consumers first (CONTRACTS.md §2).

Known limitations:

- Presence is tracked in memory by the API process, which is correct for the single instance deployed today. Running several API instances requires a shared store (for example a Redis backplane and a shared presence set).
- Someone who joins a community after connecting announces their presence there only on their next connection; others see them after re-subscribing.
- A subscription is checked when it is made. If someone loses access to a channel (left or removed from the community), they keep receiving its events until they reconnect.
