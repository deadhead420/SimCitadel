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

        public async Task Handle(IGluonSession session, NotifyLotRoommateChange packet)
        {
            // received from a lot server to notify of another lot's roommate change.
            using (var da = DAFactory.Get())
            {
                var lot = da.Lots.Get(packet.LotId);
                if (lot == null) return; // lot missing

                DataService.Invalidate<FSO.Common.DataService.Model.Lot>(lot.location);

                // if online, notify the lot
                var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                if (lotOwned != null)
                {
                    var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                    if (lotServer != null)
                    {
                        // immediately notify lot of new roommate
                        lotServer.Write(packet);
                    }
                }
                else
                {
                    // try force the lot open
                    // we don't need to send any packets in this case - the lot fully restores object ownership from db.
                    var result = await Lots.TryFindOrOpen(lot.location, 0, NullSecurityContext.INSTANCE);
                }
            }
        }

        public async Task Handle(IVoltronSession session, ChangeRoommateRequest packet)
        {
            try
            {
                if (session.IsAnonymous) return;

                using (var da = DAFactory.Get())
                {
                    if (packet.Type == ChangeRoommateType.POLL)
                    {
                        var lots = da.Roommates.GetAvatarsLots(session.AvatarId);
                        foreach (var lot in lots)
                        {
                            if (lot.is_pending == 1)
                            {
                                var lotdb = da.Lots.Get(lot.lot_id);
                                if (lotdb == null) return;

                                session.Write(new ChangeRoommateRequest
                                {
                                    Type = ChangeRoommateType.INVITE,
                                    AvatarId = lotdb.owner_id ?? 0,
                                    LotLocation = lotdb.location
                                });
                            }
                        }
                    }
                    else if (packet.Type == ChangeRoommateType.ACCEPT)
                    {
                        var lot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                        if (lot == null) 
                        { 
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST); 
                            return; 
                        }

                        if (da.Roommates.AcceptRoommateRequest(session.AvatarId, lot.lot_id))
                        {
                            var lotDS = await DataService.Get<FSO.Common.DataService.Model.Lot>(packet.LotLocation);
                            if (lotDS != null) lotDS.Lot_RoommateVec = lotDS.Lot_RoommateVec.Add(session.AvatarId);

                            var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                            if (lotOwned != null)
                            {
                                var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                                if (lotServer != null)
                                {
                                    lotServer.Write(new NotifyLotRoommateChange()
                                    {
                                        AvatarId = session.AvatarId,
                                        LotId = lot.lot_id,
                                        Change = Protocol.Gluon.Model.ChangeType.ADD_ROOMMATE
                                    });
                                }
                            }

                            var avatar = await DataService.Get<Avatar>(session.AvatarId);
                            var currentLots = da.Roommates.GetAvatarsLots(session.AvatarId);
                            if (currentLots.Count <= 1)
                            {
                                da.Avatars.UpdateMoveDate(session.AvatarId, Epoch.Now);
                            }

                            Status(session, ChangeRoommateResponseStatus.ACCEPT_SUCCESS); 
                            return;
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING); 
                            return;
                        }
                    }
                    else if (packet.Type == ChangeRoommateType.DECLINE)
                    {
                        var lot = da.Lots.GetByLocation(Context.ShardId, packet.LotLocation);
                        if (lot == null) 
                        { 
                            Status(session, ChangeRoommateResponseStatus.LOT_DOESNT_EXIST); 
                            return; 
                        }

                        if (da.Roommates.DeclineRoommateRequest(session.AvatarId, lot.lot_id))
                        {
                            Status(session, ChangeRoommateResponseStatus.DECLINE_SUCCESS);
                            return;
                        }
                        else
                        {
                            Status(session, ChangeRoommateResponseStatus.NO_INVITE_PENDING);
                            return;
                        }
                    }
                    else if (packet.Type == ChangeRoommateType.INVITE)
                    {
                        uint loc = packet.LotLocation;
                        DbLot targetLot = (loc != 0) ? da.Lots.GetByLocation(Context.ShardId, loc) : null;

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

                        var targ = da.Avatars.Get(packet.AvatarId);
                        if (targ == null)
                        {
                            Status(session, ChangeRoommateResponseStatus.UNKNOWN);
                            return;
                        }

                        var myLotRoomies = da.Roommates.GetLotRoommates(targetLot.lot_id);
                        if (myLotRoomies.Count >= 8)
                        {
                            var pending = myLotRoomies.FirstOrDefault(x => x.is_pending == 1);
                            if (pending == null)
                            {
                                Status(session, ChangeRoommateResponseStatus.TOO_MANY_ROOMMATES);
                                return;
                            }
                            else
                            {
                                da.Roommates.DeclineRoommateRequest(pending.avatar_id, pending.lot_id);
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
                                targetSession.Write(new ChangeRoommateRequest()
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
                    else if (packet.Type == ChangeRoommateType.KICK)
                    {
                        var result = await TryKick(packet.LotLocation, session.AvatarId, packet.AvatarId);
                        Status(session, result);
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
                    var selfDelete = false;
                    if (requester == target)
                    {
                        selfDelete = true;
                    }
                    else if (lot.owner_id != requester)
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

                    var lotOwned = da.LotClaims.GetByLotID(lot.lot_id);
                    if (lotOwned != null)
                    {
                        var lotServer = LotServers.GetLotServerSession(lotOwned.owner);
                        if (lotServer != null)
                        {
                            lotServer.Write(new NotifyLotRoommateChange()
                            {
                                AvatarId = target,
                                LotId = lot.lot_id,
                                Change = Protocol.Gluon.Model.ChangeType.REMOVE_ROOMMATE
                            });
                        }
                    }
                    else
                    {
                        var result = await Lots.TryFindOrOpen(lot.location, 0, NullSecurityContext.INSTANCE);
                    }

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
                                targetSession.Write(new ChangeRoommateResponse()
                                {
                                    Type = (kickedMe) ? ChangeRoommateResponseStatus.GOT_KICKED : ChangeRoommateResponseStatus.ROOMMATE_LEFT,
                                    Extra = target
                                });
                            }
                        }
                    }

                    if (selfDelete) return ChangeRoommateResponseStatus.SELFKICK_SUCCESS;
                    else return ChangeRoommateResponseStatus.KICK_SUCCESS;
                }
                else
                {
                    return ChangeRoommateResponseStatus.YOU_ARE_NOT_ROOMMATE;
                }
            }
        }
    }
}
