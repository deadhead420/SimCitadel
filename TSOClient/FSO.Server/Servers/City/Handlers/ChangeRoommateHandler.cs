using FSO.Common.DataService;
using FSO.Common.DataService.Model;
using FSO.Common.Security;
using FSO.Server.Common;
using FSO.Server.Database.DA;
using FSO.Server.Database.DA.Lots;
using FSO.Server.Database.DA.Roommates;
using FSO.Server.Framework.Aries;
using FSO.Server.Framework.Gluon;
using FSO.Server.Framework.Voltron;
using FSO.Server.Protocol.Electron.Model;
using FSO.Server.Protocol.Electron.Packets;
using FSO.Server.Protocol.Gluon.Packets;
using FSO.Server.Servers.City.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FSO.Server.Servers.City.Handlers
{
    public class ChangeRoommateHandler
    {
        private IDAFactory DAFactory;
        private CityServerContext Context;
        private ISessions Sessions;
        private IDataService DataService;
        private LotServerPicker LotServers;
        private LotAllocations Lots;

        public ChangeRoommateHandler(ISessions sessions, IDAFactory da, CityServerContext context, IDataService dataService, LotServerPicker lotServers, LotAllocations lots)
        {
            this.Sessions = sessions;
            this.DAFactory = da;
            this.Context = context;
            this.DataService = dataService;
            this.LotServers = lotServers;
            this.Lots = lots;
        }

        private void Status(IVoltronSession session, ChangeRoommateResponseStatus status)
        {
            session.Write(new ChangeRoommateResponse { Type = status });
        }

        private void NotifyLotServer(IDA da, int lotId, uint avatarId, Protocol.Gluon.Model.ChangeType changeType)
        {
            var lotOwned = da.LotClaims.GetByLotID(lotId);
            if (lotOwned != null)
            {
                var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                if (lotServer != null)
                {
                    lotServer.Write(new NotifyLotRoommateChange
                    {
                        AvatarId = avatarId,
                        LotId = lotId,
                        Change = changeType
                    });
                }
            }
        }

        public async void Handle(IGluonSession session, NotifyLotRoommateChange packet)
        {
            using (var da = DAFactory.Get())
            {
                var lot = da.Lots.Get(packet.LotId);
                if (lot == null) return;

                DataService.Invalidate<FSO.Common.DataService.Model.Lot>(lot.location);

                var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                if (lotOwned != null)
                {
                    var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                    if (lotServer != null)
                    {
                        lotServer.Write(packet);
                    }
                }
                else
                {
                    await Lots.TryFindOrOpen(lot.location, 0, NullSecurityContext.INSTANCE);
                }
            }
        }

        public async void Handle(IVoltronSession session, ChangeRoommateRequest packet)
        {
            try
            {
                if (session.IsAnonymous) return;

                using (var da = DAFactory.Get())
                {
                    // 1. POLL - Check invites across all lots
                    if (packet.Type == ChangeRoommateType.POLL)
                    {
                        var myLots = da.Roommates.GetAvatarsLots(session.AvatarId);
                        foreach (var lotLink in myLots)
                        {
                            if (lotLink.is_pending == 1)
                            {
                                var lotdb = da.Lots.Get(lotLink.lot_id);
                                if (lotdb == null) continue;

                                session.Write(new ChangeRoommateRequest
                                {
                                    Type = ChangeRoommateType.INVITE,
                                    AvatarId = lotdb.owner_id ?? 0,
                                    LotLocation = lotdb.location
                                });
                            }
                        }
                        return;
                    }

                    // 2. TARGET LOT RESOLUTION
                    DbLot targetLot = null;

                    if (packet.LotLocation != 0)
                    {
                        targetLot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                    }
                    else
                    {
                        var ownedLots = da.Lots.GetByOwner(session.AvatarId);
                        if (ownedLots != null && ownedLots.Count == 1)
                        {
                            targetLot = ownedLots[0];
                        }
                    }

                    // 3. ACCEPT / DECLINE
                    if (packet.Type == ChangeRoommateType.ACCEPT)
                    {
                        if (targetLot == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST);
                            return;
                        }

                        if (da.Roommates.AcceptRoommateRequest(session.AvatarId, targetLot.lot_id))
                        {
                            var lotDS = await DataService.Get<FSO.Common.DataService.Model.Lot>(targetLot.location);
                            if (lotDS != null) lotDS.Lot_RoommateVec = lotDS.Lot_RoommateVec.Add(session.AvatarId);

                            NotifyLotServer(da, targetLot.lot_id, session.AvatarId, Protocol.Gluon.Model.ChangeType.ADD_ROOMMATE);

                            var currentLots = da.Roommates.GetAvatarsLots(session.AvatarId);
                            if (currentLots.Count <= 1)
                            {
                                da.Avatars.UpdateMoveDate(session.AvatarId, Epoch.Now);
                            }

                            Status(session, ChangeRoommateResponseStatus.ACCEPT_SUCCESS);
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING);
                        }
                        return;
                    }

                    if (packet.Type == ChangeRoommateType.DECLINE)
                    {
                        if (targetLot == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST);
                            return;
                        }

                        if (da.Roommates.DeclineRoommateRequest(session.AvatarId, targetLot.lot_id))
                        {
                            Status(session, ChangeRoommateResponseStatus.DECLINE_SUCCESS);
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING);
                        }
                        return;
                    }

                    // 4. INVITE
                    if (packet.Type == ChangeRoommateType.INVITE)
                    {
                        if (targetLot == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST);
                            return;
                        }

                        if (targetLot.owner_id != session.AvatarId)
                        {
                            Status(session, ChangeRoommateResponseStatus.YOU_ARE_NOT_OWNER);
                            return;
                        }

                        var targetAvatar = da.Avatars.Get(packet.AvatarId);
                        if (targetAvatar == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.UNKNOWN);
                            return;
                        }

                        var currentRoomies = da.Roommates.GetLotRoommates(targetLot.lot_id);
                        if (currentRoomies.Count >= 8)
                        {
                            var pendingReq = currentRoomies.FirstOrDefault(x => x.is_pending == 1);
                            if (pendingReq == null)
                            {
                                Status(session, ChangeRoommateResponseStatus.TOO_MANY_ROOMMATES);
                                return;
                            }
                            else
                            {
                                da.Roommates.DeclineRoommateRequest(pendingReq.avatar_id, pendingReq.lot_id);
                            }
                        }

                        bool created = da.Roommates.Create(new DbRoommate
                        {
                            avatar_id = packet.AvatarId,
                            lot_id = targetLot.lot_id,
                            is_pending = 1,
                            permissions_level = 0
                        });

                        if (created)
                        {
                            var targetSession = Sessions.GetByAvatarId(packet.AvatarId);
                            if (targetSession != null)
                            {
                                targetSession.Write(new ChangeRoommateRequest
                                {
                                    Type = ChangeRoommateType.INVITE,
                                    AvatarId = session.AvatarId,
                                    LotLocation = targetLot.location
                                });
                            }

                            Status(session, ChangeRoommateResponseStatus.INVITE_SUCCESS);
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.UNKNOWN);
                        }
                        return;
                    }

                    // 5. KICK
                    if (packet.Type == ChangeRoommateType.KICK)
                    {
                        if (targetLot == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST);
                            return;
                        }

                        var result = await TryKick(targetLot.location, session.AvatarId, packet.AvatarId);
                        Status(session, result);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                Status(session, ChangeRoommateResponseStatus.UNKNOWN);
            }
        }

        public async Task<ChangeRoommateResponseStatus> TryKick(uint location, uint requester, uint target)
        {
            using (var da = DAFactory.Get())
            {
                var lot = da.Lots.GetByLocation(Context.ShardId, location);
                if (lot == null) return ChangeRoommateResponseStatus.UNKNOWN;

                var roommates = da.Roommates.GetLotRoommates(lot.lot_id);

                if (roommates.Any(x => x.avatar_id == target && x.is_pending == 0))
                {
                    var selfDelete = (requester == target);
                    if (!selfDelete && lot.owner_id != requester)
                    {
                        return ChangeRoommateResponseStatus.YOU_ARE_NOT_OWNER;
                    }

                    if (da.Roommates.RemoveRoommate(target, lot.lot_id) == 0)
                    {
                        return ChangeRoommateResponseStatus.YOU_ARE_NOT_ROOMMATE;
                    }

                    if (selfDelete && lot.owner_id == requester)
                    {
                        da.Lots.ReassignOwner(lot.lot_id);
                    }

                    DataService.Invalidate<FSO.Common.DataService.Model.Lot>(location);

                    NotifyLotServer(da, lot.lot_id, target, Protocol.Gluon.Model.ChangeType.REMOVE_ROOMMATE);

                    var avatar = await DataService.Get<Avatar>(target);
                    if (avatar != null) avatar.Avatar_LotGridXY = 0;

                    foreach (var roomie in roommates)
                    {
                        var kickedMe = roomie.avatar_id == target;
                        if (roomie.is_pending == 0 && !(kickedMe && selfDelete) && target != roomie.avatar_id)
                        {
                            var targetSession = Sessions.GetByAvatarId(roomie.avatar_id);
                            if (targetSession != null)
                            {
                                targetSession.Write(new ChangeRoommateResponse
                                {
                                    Type = kickedMe ? ChangeRoommateResponseStatus.GOT_KICKED : ChangeRoommateResponseStatus.ROOMMATE_LEFT,
                                    Extra = target
                                });
                            }
                        }
                    }

                    return selfDelete ? ChangeRoommateResponseStatus.SELFKICK_SUCCESS : ChangeRoommateResponseStatus.KICK_SUCCESS;
                }
                else
                {
                    return ChangeRoommateResponseStatus.YOU_ARE_NOT_ROOMMATE;
                }
            }
        }
    }
}
