using OpenMetaverse;
using OpenMetaverse.Packets;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Simian.Extensions
{
    public class Movement : ISimianExtension
    {
        const int UPDATE_ITERATION = 100;
        const float WALK_SPEED = 3f;
        const float RUN_SPEED = 6f;
        const float FLY_SPEED = 12f;
        const float FALL_FORGIVENESS = 0.5f;
        const float SQRT_TWO = 1.41421356f;

        Simian server;
        AvatarManager Avatars;
        Timer updateTimer;
        long lastTick;

        public int LastTick
        {
            get { return (int) Interlocked.Read(ref lastTick); }
            set { Interlocked.Exchange(ref lastTick, value); }
        }

        public Movement(Simian server)
        {
            this.server = server;
        }

        public void Start()
        {
            server.UDPServer.RegisterPacketCallback(PacketType.AgentUpdate, new UDPServer.PacketCallback(AgentUpdateHandler));
            server.UDPServer.RegisterPacketCallback(PacketType.AgentHeightWidth, new UDPServer.PacketCallback(AgentHeightWidthHandler));
            server.UDPServer.RegisterPacketCallback(PacketType.SetAlwaysRun, new UDPServer.PacketCallback(SetAlwaysRunHandler));

            updateTimer = new Timer(new TimerCallback(UpdateTimer_Elapsed));
            LastTick = Environment.TickCount;
            updateTimer.Change(UPDATE_ITERATION, UPDATE_ITERATION);
        }

        public void Stop()
        {
            updateTimer.Dispose();
        }

        void UpdateTimer_Elapsed(object sender)
        {
            int tick = Environment.TickCount;
            float seconds = (float)((tick  - LastTick) / 1000f);
            LastTick = tick;

            lock (server.Agents)
            {
                foreach (Agent agent in server.Agents.Values)
                {
                    bool animsChanged = false;

                    // Create forward and left vectors from the current avatar rotation
                    Matrix4 rotMatrix = Matrix4.CreateFromQuaternion(agent.Avatar.Rotation);
                    Vector3 fwd = Vector3.Transform(Vector3.UnitX, rotMatrix);
                    Vector3 left = Vector3.Transform(Vector3.UnitY, rotMatrix);

                    // Check control flags
                    bool heldForward = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) == AgentManager.ControlFlags.AGENT_CONTROL_AT_POS;
                    bool heldBack = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) == AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG;
                    bool heldLeft = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) == AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS;
                    bool heldRight = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) == AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG;
                    bool heldTurnLeft = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_LEFT) == AgentManager.ControlFlags.AGENT_CONTROL_TURN_LEFT;
                    bool heldTurnRight = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_RIGHT) == AgentManager.ControlFlags.AGENT_CONTROL_TURN_RIGHT;
                    bool heldUp = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) == AgentManager.ControlFlags.AGENT_CONTROL_UP_POS;
                    bool heldDown = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) == AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG;
                    bool flying = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) == AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                    bool mouselook = (agent.ControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) == AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK;
                    bool falling = false;

                    float speed = seconds * (flying ? FLY_SPEED : agent.Running ? RUN_SPEED : WALK_SPEED);

                    Vector3 move = Vector3.Zero;

                    if (heldForward) { move.X += fwd.X; move.Y += fwd.Y; }
                    if (heldBack) { move.X -= fwd.X; move.Y -= fwd.Y; }
                    if (heldLeft) { move.X += left.X; move.Y += left.Y; }
                    if (heldRight) { move.X -= left.X; move.Y -= left.Y; }

                    float oldFloor = GetLandHeightAt(agent.Avatar.Position);
                    float newFloor = GetLandHeightAt(agent.Avatar.Position + move);
                    float lowerLimit = newFloor + agent.Avatar.Scale.Z / 2;

                    if ((heldForward || heldBack) && (heldLeft || heldRight))
                        speed /= SQRT_TWO;

                    if (!flying && newFloor != oldFloor) speed /= (1 + (SQRT_TWO * Math.Abs(newFloor - oldFloor)));

                    if (flying)
                    {
                        if (heldUp)
                            move.Z += speed;

                        if (heldDown)
                            move.Z -= speed;
                    }
                    else if (agent.Avatar.Position.Z > lowerLimit)
                    {
                        agent.Avatar.Position.Z -= 9.8f * seconds;
                        agent.Avatar.Position.Z -= 9.8f * seconds;

                        if (agent.Avatar.Position.Z > lowerLimit + FALL_FORGIVENESS)
                            falling = true;
                    }
                    else agent.Avatar.Position.Z = lowerLimit;

                    bool movingHorizontally =
                        (agent.Avatar.Velocity.X * agent.Avatar.Velocity.X) +
                        (agent.Avatar.Velocity.Y * agent.Avatar.Velocity.Y) > 0f;

                    if (flying)
                    {
                        agent.Avatar.Acceleration = move * speed;
                        if (movingHorizontally)
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.FLY))
                                animsChanged = true;
                        }
                        else if (heldUp && !heldDown)
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.HOVER_UP))
                                animsChanged = true;
                        }
                        else if (heldDown && !heldUp)
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.HOVER_DOWN))
                                animsChanged = true;
                        }
                        else
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.HOVER))
                                animsChanged = true;
                        }
                    }
                    else if (falling)
                    {
                        agent.Avatar.Acceleration /= 1 + (seconds / SQRT_TWO);

                        if (server.Avatars.SetDefaultAnimation(agent, Animations.FALLDOWN))
                            animsChanged = true;
                    }
                    else //on the ground
                    {
                        agent.Avatar.Acceleration = move * speed;
                        if (movingHorizontally)
                        {
                            if (heldDown)
                            {
                                if (server.Avatars.SetDefaultAnimation(agent, Animations.CROUCHWALK))
                                    animsChanged = true;
                            }
                            else if (agent.Running)
                            {
                                if (server.Avatars.SetDefaultAnimation(agent, Animations.RUN))
                                    animsChanged = true;
                            }
                            else
                            {
                                if (server.Avatars.SetDefaultAnimation(agent, Animations.WALK))
                                    animsChanged = true;
                            }
                        }
                        else if (heldDown)
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.CROUCH))
                                animsChanged = true;
                        }
                        else
                        {
                            if (server.Avatars.SetDefaultAnimation(agent, Animations.STAND))
                                animsChanged = true;
                        }
                    }

                    if (animsChanged)
                        server.Avatars.SendAnimations(agent);

                    agent.Avatar.Velocity = agent.Avatar.Acceleration;
                    agent.Avatar.Position += agent.Avatar.Velocity;

                    if (agent.Avatar.Position.X < 0) agent.Avatar.Position.X = 0f;
                    else if (agent.Avatar.Position.X > 255) agent.Avatar.Position.X = 255f;

                    if (agent.Avatar.Position.Y < 0) agent.Avatar.Position.Y = 0f;
                    else if (agent.Avatar.Position.Y > 255) agent.Avatar.Position.Y = 255f;

                    if (agent.Avatar.Position.Z < lowerLimit) agent.Avatar.Position.Z = lowerLimit;

                }
            }
        }

        void AgentUpdateHandler(Packet packet, Agent agent)
        {
            AgentUpdatePacket update = (AgentUpdatePacket)packet;

            agent.Avatar.Rotation = update.AgentData.BodyRotation;
            agent.ControlFlags = (AgentManager.ControlFlags)update.AgentData.ControlFlags;
            agent.State = update.AgentData.State;
            agent.Flags = (PrimFlags)update.AgentData.Flags;

            ObjectUpdatePacket fullUpdate = BuildFullUpdate(agent.Avatar,
                NameValue.NameValuesToString(agent.Avatar.NameValues), server.RegionHandle,
                agent.State, agent.Flags);

            lock (server.Agents)
            {
                foreach (Agent recipient in server.Agents.Values)
                    recipient.SendPacket(fullUpdate);
            }
        }

        void SetAlwaysRunHandler(Packet packet, Agent agent)
        {
            SetAlwaysRunPacket run = (SetAlwaysRunPacket)packet;

            agent.Running = run.AgentData.AlwaysRun;
        }

        float GetLandHeightAt(Vector3 position)
        {
            int x = (int)position.X;
            int y = (int)position.Y;

            if (x > 255) x = 255;
            else if (x < 0) x = 0;
            if (y > 255) y = 255;
            else if (y < 0) y = 0;

            float center = server.Heightmap[y * 256 + x];
            float distX = position.X - (int)position.X;
            float distY = position.Y - (int)position.Y;

            float nearestX;
            float nearestY;

            if (distX > 0) nearestX = server.Heightmap[y * 256 + x + (x < 255 ? 1 : 0)];
            else nearestX = server.Heightmap[y * 256 + x - (x > 0 ? 1 : 0)];

            if (distY > 0) nearestY = server.Heightmap[(y + (y < 255 ? 1 : 0)) * 256 + x];
            else nearestY = server.Heightmap[(y - (y > 0 ? 1 : 0)) * 256 + x];

            float lerpX = Utils.Lerp(center, nearestX, Math.Abs(distX));
            float lerpY = Utils.Lerp(center, nearestY, Math.Abs(distY));

            return ((lerpX + lerpY) / 2);
        }

        void AgentHeightWidthHandler(Packet packet, Agent agent)
        {
            AgentHeightWidthPacket heightWidth = (AgentHeightWidthPacket)packet;

            Logger.Log(String.Format("Agent wants to set height={0}, width={1}",
                heightWidth.HeightWidthBlock.Height, heightWidth.HeightWidthBlock.Width), Helpers.LogLevel.Info);
        }

        public static ObjectUpdatePacket BuildFullUpdate(Primitive obj, string nameValues, ulong regionHandle,
            byte state, PrimFlags flags)
        {
            byte[] objectData = new byte[60];
            int pos = 0;
            obj.Position.GetBytes().CopyTo(objectData, pos);
            pos += 12;
            obj.Velocity.GetBytes().CopyTo(objectData, pos);
            pos += 12;
            obj.Acceleration.GetBytes().CopyTo(objectData, pos);
            pos += 12;
            obj.Rotation.GetBytes().CopyTo(objectData, pos);
            pos += 12;
            obj.AngularVelocity.GetBytes().CopyTo(objectData, pos);

            ObjectUpdatePacket update = new ObjectUpdatePacket();
            update.RegionData.RegionHandle = regionHandle;
            update.RegionData.TimeDilation = UInt16.MaxValue;
            update.ObjectData = new ObjectUpdatePacket.ObjectDataBlock[1];
            update.ObjectData[0] = new ObjectUpdatePacket.ObjectDataBlock();
            update.ObjectData[0].ClickAction = (byte)obj.ClickAction;
            update.ObjectData[0].CRC = 0;
            update.ObjectData[0].ExtraParams = new byte[0]; //FIXME: Need a serializer for ExtraParams
            update.ObjectData[0].Flags = (byte)flags;
            update.ObjectData[0].FullID = obj.ID;
            update.ObjectData[0].Gain = obj.SoundGain;
            update.ObjectData[0].ID = obj.LocalID;
            update.ObjectData[0].JointAxisOrAnchor = obj.JointAxisOrAnchor;
            update.ObjectData[0].JointPivot = obj.JointPivot;
            update.ObjectData[0].JointType = (byte)obj.Joint;
            update.ObjectData[0].Material = (byte)obj.PrimData.Material;
            update.ObjectData[0].MediaURL = new byte[0]; // FIXME:
            update.ObjectData[0].NameValue = Utils.StringToBytes(nameValues);
            update.ObjectData[0].ObjectData = objectData;
            update.ObjectData[0].OwnerID = obj.Properties.OwnerID;
            update.ObjectData[0].ParentID = obj.ParentID;
            update.ObjectData[0].PathBegin = Primitive.PackBeginCut(obj.PrimData.PathBegin);
            update.ObjectData[0].PathCurve = (byte)obj.PrimData.PathCurve;
            update.ObjectData[0].PathEnd = Primitive.PackEndCut(obj.PrimData.PathEnd);
            update.ObjectData[0].PathRadiusOffset = Primitive.PackPathTwist(obj.PrimData.PathRadiusOffset);
            update.ObjectData[0].PathRevolutions = Primitive.PackPathRevolutions(obj.PrimData.PathRevolutions);
            update.ObjectData[0].PathScaleX = Primitive.PackPathScale(obj.PrimData.PathScaleX);
            update.ObjectData[0].PathScaleY = Primitive.PackPathScale(obj.PrimData.PathScaleY);
            update.ObjectData[0].PathShearX = (byte)Primitive.PackPathShear(obj.PrimData.PathShearX);
            update.ObjectData[0].PathShearY = (byte)Primitive.PackPathShear(obj.PrimData.PathShearY);
            update.ObjectData[0].PathSkew = Primitive.PackPathTwist(obj.PrimData.PathSkew);
            update.ObjectData[0].PathTaperX = Primitive.PackPathTaper(obj.PrimData.PathTaperX);
            update.ObjectData[0].PathTaperY = Primitive.PackPathTaper(obj.PrimData.PathTaperY);
            update.ObjectData[0].PathTwist = Primitive.PackPathTwist(obj.PrimData.PathTwist);
            update.ObjectData[0].PathTwistBegin = Primitive.PackPathTwist(obj.PrimData.PathTwistBegin);
            update.ObjectData[0].PCode = (byte)obj.PrimData.PCode;
            update.ObjectData[0].ProfileBegin = Primitive.PackBeginCut(obj.PrimData.ProfileBegin);
            update.ObjectData[0].ProfileCurve = (byte)obj.PrimData.ProfileCurve;
            update.ObjectData[0].ProfileEnd = Primitive.PackEndCut(obj.PrimData.ProfileEnd);
            update.ObjectData[0].ProfileHollow = Primitive.PackProfileHollow(obj.PrimData.ProfileHollow);
            update.ObjectData[0].PSBlock = new byte[0]; // FIXME:
            update.ObjectData[0].TextColor = obj.TextColor.GetBytes(true);
            update.ObjectData[0].TextureAnim = obj.TextureAnim.GetBytes();
            update.ObjectData[0].TextureEntry = obj.Textures.ToBytes();
            update.ObjectData[0].Radius = obj.SoundRadius;
            update.ObjectData[0].Scale = obj.Scale;
            update.ObjectData[0].Sound = obj.Sound;
            update.ObjectData[0].State = state;
            update.ObjectData[0].Text = Utils.StringToBytes(obj.Text);
            update.ObjectData[0].UpdateFlags = (uint)flags;
            update.ObjectData[0].Data = new byte[0]; // FIXME:

            return update;
        }
    }
}