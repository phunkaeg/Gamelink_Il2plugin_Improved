// IL2Plugin.cs — v1.4 dual-stream
//
// Motion stream (UDP 4321):
//   - Keeps the v1.3 processed motion channels for Yaw / Pitch / Roll cueing.
//
// Telemetry stream (UDP 4322):
//   - Adds continuous telemetry-driven inputs for engine/cockpit shake and other indicators.
//   - Adds event-envelope inputs for gunfire, hits, damage, explosion, bomb drop, and rocket launch.
//
// Recommended GameLink mapping:
//   YAW   -> Yaw_Integrated
//   PITCH -> Pitch_Soft
//   ROLL  -> Roll_Processed
//
// Useful vibration sources:
//   ENG_SHAKE_FRQ / ENG_SHAKE_AMP
//   COCKPIT_SHAKE (freq + amp)
//   Gun_Fire_Light / Gun_Fire_Heavy
//   Hit_Shock / Damage_Shock / Explosion_Shock

using IL2Plugin;
using IL2Plugin.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using YawGLAPI;

#nullable disable
namespace YawVR_Game_Engine.Plugin
{
    [Export(typeof(Game))]
    [ExportMetadata("Name", "IL2")]
    [ExportMetadata("Version", "1.4")]
    internal class IL2Plugin : Game
    {
        // Motion tuning
        private const float SOFT_ZONE = 0.65f;
        private const float ROLL_LIMIT = 15f;
        private const float PITCH_FWD_LIMIT = 15f;
        private const float PITCH_BWD_LIMIT = 45f;
        private const float YAW_INTEGRATED_LIMIT = 90f;
        private const float YAW_ACCUMULATOR_CLAMP = YAW_INTEGRATED_LIMIT * 3f;
        private const float BARREL_ROLL_RATE_MIN = 80f;
        private const float BARREL_ROLL_RATE_MAX = 220f;
        private const float BARREL_ROLL_RATE_SCALE = 0.07f;

        // Dynamic decay while paused / idle
        private const float DYNAMIC_PAUSE_DECAY_LAMBDA = 10f;

        // Event envelope decay rates (larger = faster decay)
        private const float GUN_FIRE_DECAY_LAMBDA = 18f;
        private const float HIT_DECAY_LAMBDA = 10f;
        private const float DAMAGE_DECAY_LAMBDA = 8f;
        private const float EXPLOSION_DECAY_LAMBDA = 6f;
        private const float BOMB_DROP_DECAY_LAMBDA = 7f;
        private const float ROCKET_LAUNCH_DECAY_LAMBDA = 10f;

        // Stream / socket behavior
        private const int TELEMETRY_PORT = 4322;
        private const int SOCKET_TIMEOUT_MS = 250;
        private const int STREAM_TIMEOUT_MS = 3000;
        private const int HOME_STEPS = 50;
        private const int HOME_STEP_MS = 40;

        // Protocol constants
        private const uint MOTION_PACKET_ID = 0x494C0100;
        private const int MOTION_PACKET_SIZE = 44;
        private const uint TELEMETRY_PACKET_ID = 0x54000101;
        private const int TELEMETRY_HEADER_MIN_SIZE = 11;

        // Telemetry indicator IDs
        private const ushort IND_ENG_RPM = 0;
        private const ushort IND_ENG_MP = 1;
        private const ushort IND_ENG_SHAKE_FRQ = 2;
        private const ushort IND_ENG_SHAKE_AMP = 3;
        private const ushort IND_LGEARS_STATE = 4;
        private const ushort IND_LGEARS_PRESS = 5;
        private const ushort IND_EAS = 6;
        private const ushort IND_AOA = 7;
        private const ushort IND_ACCELERATION = 8;
        private const ushort IND_COCKPIT_SHAKE = 9;
        private const ushort IND_AGL = 10;
        private const ushort IND_FLAPS = 11;
        private const ushort IND_AIR_BRAKES = 12;

        // Telemetry event IDs
        private const ushort EVT_SET_FOCUS = 0;
        private const ushort EVT_SETUP_ENG = 1;
        private const ushort EVT_SETUP_GUN = 2;
        private const ushort EVT_SETUP_LGEAR = 3;
        private const ushort EVT_DROP_BOMB = 4;
        private const ushort EVT_ROCKET_LAUNCH = 5;
        private const ushort EVT_HIT = 6;
        private const ushort EVT_DAMAGE = 7;
        private const ushort EVT_EXPLOSION = 8;
        private const ushort EVT_GUN_FIRE = 9;

        private readonly object stateLock = new object();

        private UdpClient motionClient;
        private UdpClient telemetryClient;
        private Thread motionThread;
        private Thread telemetryThread;
        private IPEndPoint motionRemote = new IPEndPoint(IPAddress.Any, 4321);
        private IPEndPoint telemetryRemote = new IPEndPoint(IPAddress.Any, TELEMETRY_PORT);
        private IMainFormDispatcher dispatcher;
        private IProfileManager controller;
        private bool stop;
        private bool returnedHome;
        private DateTime lastMotionPacketTime = DateTime.MinValue;
        private DateTime lastDecayTime = DateTime.MinValue;

        // Motion state
        private uint lastMotionTick;
        private float lastYawRad;
        private bool hadFirstFrame;
        private float integratedYaw;

        // Motion outputs
        private float dispYaw;
        private float dispPitch;
        private float dispRoll;
        private float dispSpinX;
        private float dispSpinY;
        private float dispSpinZ;
        private float dispAccX;
        private float dispAccY;
        private float dispAccZ;
        private float dispYawDelta;
        private float dispRollProcessed;
        private float dispPitchSoft;
        private float dispYawIntegrated;

        // Continuous telemetry outputs
        private float telEngRpmAvg;
        private float telEngMpAvg;
        private float telEngShakeFreq;
        private float telEngShakeAmp;
        private float telCockpitShakeFreq;
        private float telCockpitShakeAmp;
        private float telAccX;
        private float telAccY;
        private float telAccZ;
        private float telEas;
        private float telAoaDeg;
        private float telAgl;
        private float telFlaps;
        private float telAirBrakes;
        private float telLGearPressAvg;

        // Event-envelope telemetry outputs
        private float telGunFireLight;
        private float telGunFireHeavy;
        private float telHitShock;
        private float telDamageShock;
        private float telExplosionShock;
        private float telBombDropKick;
        private float telRocketLaunchKick;

        private readonly Dictionary<short, GunSetup> gunSetups = new Dictionary<short, GunSetup>();

        private struct GunSetup
        {
            public short Index;
            public float ProjectileMass;
            public float ShootVelocity;
        }

        public int STEAM_ID => 307960;
        public string AUTHOR => "YawVR (modified dual-stream)";
        public string PROCESS_NAME => string.Empty;
        public bool PATCH_AVAILABLE => false;
        public string Description => Resources.description;
        public Stream Logo => GetStream("logo.png");
        public Stream SmallLogo => GetStream("recent.png");
        public Stream Background => GetStream("wide.png");

        public LedEffect DefaultLED() => new LedEffect((EFFECT_TYPE)3, 2, new YawColor[4]
        {
            new YawColor(66, 135, 245),
            new YawColor(80, 80, 80),
            new YawColor(128, 3, 117),
            new YawColor(110, 201, 12)
        }, 0.7f);

        public List<Profile_Component> DefaultProfile() => new List<Profile_Component>()
        {
            new Profile_Component(0,  0, 1f, 1f, 0f, false, false, -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
            new Profile_Component(1,  1, 1f, 1f, 0f, false, false, -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
            new Profile_Component(2,  2, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
            new Profile_Component(3,  2, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
            new Profile_Component(4,  1, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
        };

        // 0-13: processed motion stream
        // 14-28: continuous telemetry indicators
        // 29-35: decaying telemetry-event envelopes
        public string[] GetInputData() => new string[36]
        {
            "Yaw",                // 0  raw absolute yaw (backward compatibility)
            "Pitch",              // 1
            "Roll",               // 2
            "Spin_X",             // 3
            "Spin_Y",             // 4
            "Spin_Z",             // 5
            "Acceleration_X",     // 6
            "Acceleration_Y",     // 7
            "Acceleration_Z",     // 8
            "Yaw_Delta",          // 9
            "Yaw_Rate",           // 10
            "Roll_Processed",     // 11
            "Pitch_Soft",         // 12
            "Yaw_Integrated",     // 13 recommended YAW output
            "Eng_RPM_Avg",        // 14
            "Eng_MP_Avg",         // 15
            "Eng_Shake_Freq",     // 16
            "Eng_Shake_Amp",      // 17
            "Cockpit_Shake_Freq", // 18
            "Cockpit_Shake_Amp",  // 19
            "Telemetry_Acc_X",    // 20
            "Telemetry_Acc_Y",    // 21
            "Telemetry_Acc_Z",    // 22
            "EAS",                // 23
            "AOA_Deg",            // 24
            "AGL",                // 25
            "Flaps",              // 26
            "Air_Brakes",         // 27
            "LGear_Press_Avg",    // 28
            "Gun_Fire_Light",     // 29
            "Gun_Fire_Heavy",     // 30
            "Hit_Shock",          // 31
            "Damage_Shock",       // 32
            "Explosion_Shock",    // 33
            "Bomb_Drop_Kick",     // 34
            "Rocket_Launch_Kick", // 35
        };

        public void SetReferences(IProfileManager controller, IMainFormDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.controller = controller;
        }

        public void Init()
        {
            stop = false;
            returnedHome = false;
            lastMotionPacketTime = DateTime.UtcNow;
            lastDecayTime = DateTime.UtcNow;
            integratedYaw = 0f;
            hadFirstFrame = false;
            gunSetups.Clear();

            int motionPort = dispatcher.GetConfigObject<Config>().Port;

            motionClient = new UdpClient(motionPort);
            motionClient.Client.ReceiveTimeout = SOCKET_TIMEOUT_MS;

            telemetryClient = new UdpClient(TELEMETRY_PORT);
            telemetryClient.Client.ReceiveTimeout = SOCKET_TIMEOUT_MS;

            motionThread = new Thread(MotionReadLoop) { IsBackground = true };
            telemetryThread = new Thread(TelemetryReadLoop) { IsBackground = true };
            motionThread.Start();
            telemetryThread.Start();
        }

        public void Exit()
        {
            stop = true;

            try { motionClient?.Close(); } catch { }
            try { telemetryClient?.Close(); } catch { }

            motionThread?.Join(1000);
            telemetryThread?.Join(1000);

            lock (stateLock)
            {
                ReturnToHomeLocked();
            }

            motionClient = null;
            telemetryClient = null;
        }

        private void MotionReadLoop()
        {
            while (!stop)
            {
                try
                {
                    byte[] data = motionClient.Receive(ref motionRemote);
                    DateTime now = DateTime.UtcNow;

                    if (data == null || data.Length < MOTION_PACKET_SIZE)
                        continue;

                    uint packetId = ReadUInt32LE(data, 0);
                    if (packetId != MOTION_PACKET_ID)
                        continue;

                    lock (stateLock)
                    {
                        lastMotionPacketTime = now;
                        returnedHome = false;
                        ApplyDecayLocked(now);
                        ParseMotionPacketLocked(data);
                        PushInputsLocked();
                    }
                }
                catch (SocketException ex)
                {
                    if (stop)
                        break;

                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        lock (stateLock)
                        {
                            DateTime now = DateTime.UtcNow;
                            ApplyDecayLocked(now);

                            if (!returnedHome && (now - lastMotionPacketTime).TotalMilliseconds > STREAM_TIMEOUT_MS)
                            {
                                ReturnToHomeLocked();
                            }
                            else
                            {
                                PushInputsLocked();
                            }
                        }

                        continue;
                    }
                }
                catch
                {
                    // Swallow unexpected packet parse errors; keep plugin alive.
                }
            }
        }

        private void TelemetryReadLoop()
        {
            while (!stop)
            {
                try
                {
                    byte[] data = telemetryClient.Receive(ref telemetryRemote);
                    if (data == null || data.Length < TELEMETRY_HEADER_MIN_SIZE)
                        continue;

                    uint packetId = ReadUInt32LE(data, 0);
                    if (packetId != TELEMETRY_PACKET_ID)
                        continue;

                    lock (stateLock)
                    {
                        ApplyDecayLocked(DateTime.UtcNow);
                        ParseTelemetryPacketLocked(data);
                        PushInputsLocked();
                    }
                }
                catch (SocketException ex)
                {
                    if (stop)
                        break;

                    if (ex.SocketErrorCode == SocketError.TimedOut)
                        continue;
                }
                catch
                {
                    // Swallow unexpected packet parse errors; keep plugin alive.
                }
            }
        }

        private void ParseMotionPacketLocked(byte[] data)
        {
            uint currentTick = ReadUInt32LE(data, 4);
            bool tickRepeated = currentTick == lastMotionTick;
            lastMotionTick = currentTick;

            float yawRad = ReadSingle(data, 8, true);
            float pitchDeg = ReadSingle(data, 12, true) * 57.29578f;
            float rollDeg = ReadSingle(data, 16, true) * 57.29578f;
            float spinX = ReadSingle(data, 20, true) * 57.29578f;
            float spinY = ReadSingle(data, 24, true) * 57.29578f;
            float spinZ = ReadSingle(data, 28, true) * 57.29578f;
            float accX = ReadSingle(data, 32, true);
            float accY = ReadSingle(data, 36, true);
            float accZ = ReadSingle(data, 40, true);
            float yawDeg = yawRad * 57.29578f;

            bool allZero = yawRad == 0f && pitchDeg == 0f && rollDeg == 0f &&
                           spinX == 0f && spinY == 0f && spinZ == 0f &&
                           accX == 0f && accY == 0f && accZ == 0f;
            bool isPaused = tickRepeated && allZero;

            float yawDelta = 0f;
            if (hadFirstFrame && !isPaused)
            {
                float raw = (yawRad - lastYawRad) * 57.29578f;
                while (raw > 180f) raw -= 360f;
                while (raw < -180f) raw += 360f;
                yawDelta = raw;
            }

            if (!isPaused)
            {
                lastYawRad = yawRad;
                hadFirstFrame = true;

                integratedYaw += yawDelta;
                integratedYaw = Clamp(integratedYaw, -YAW_ACCUMULATOR_CLAMP, YAW_ACCUMULATOR_CLAMP);

                dispYaw = yawDeg;
                dispPitch = pitchDeg;
                dispRoll = rollDeg;
                dispSpinX = spinX;
                dispSpinY = spinY;
                dispSpinZ = spinZ;
                dispAccX = accX;
                dispAccY = accY;
                dispAccZ = accZ;
                dispYawDelta = yawDelta;
                dispYawIntegrated = SoftLimit(integratedYaw, YAW_INTEGRATED_LIMIT, SOFT_ZONE);

                float rollAngleSoft = SoftLimit(rollDeg, ROLL_LIMIT, SOFT_ZONE);
                float rollRateFeel = SoftLimit(spinX * BARREL_ROLL_RATE_SCALE, ROLL_LIMIT, SOFT_ZONE);
                float barrelBlend = Clamp01((Math.Abs(spinX) - BARREL_ROLL_RATE_MIN) / (BARREL_ROLL_RATE_MAX - BARREL_ROLL_RATE_MIN));
                dispRollProcessed = Lerp(rollAngleSoft, rollRateFeel, barrelBlend);
                dispPitchSoft = SoftLimitAsymmetric(pitchDeg, PITCH_FWD_LIMIT, PITCH_BWD_LIMIT, SOFT_ZONE);
            }
            else
            {
                // Hold pose channels during pause so the rig does not snap back to neutral.
                // Only decay dynamic channels.
            }
        }

        private void ParseTelemetryPacketLocked(byte[] data)
        {
            int offset = 10;
            if (offset >= data.Length)
                return;

            byte indicatorCount = data[offset++];

            for (int i = 0; i < indicatorCount; i++)
            {
                if (offset + 3 > data.Length)
                    return;

                ushort indicatorId = ReadUInt16LE(data, offset);
                byte valuesCount = data[offset + 2];
                int payloadOffset = offset + 3;
                int payloadSize = valuesCount * 4;

                if (payloadOffset + payloadSize > data.Length)
                    return;

                switch (indicatorId)
                {
                    case IND_ENG_RPM:
                        telEngRpmAvg = ReadAverageFloatArray(data, payloadOffset, valuesCount);
                        break;
                    case IND_ENG_MP:
                        telEngMpAvg = ReadAverageFloatArray(data, payloadOffset, valuesCount);
                        break;
                    case IND_ENG_SHAKE_FRQ:
                        telEngShakeFreq = ReadAverageFloatArray(data, payloadOffset, valuesCount);
                        break;
                    case IND_ENG_SHAKE_AMP:
                        telEngShakeAmp = ReadAverageFloatArray(data, payloadOffset, valuesCount);
                        break;
                    case IND_LGEARS_PRESS:
                        telLGearPressAvg = ReadAverageFloatArray(data, payloadOffset, valuesCount);
                        break;
                    case IND_EAS:
                        telEas = valuesCount >= 1 ? ReadSingle(data, payloadOffset, true) : telEas;
                        break;
                    case IND_AOA:
                        telAoaDeg = valuesCount >= 1 ? ReadSingle(data, payloadOffset, true) * 57.29578f : telAoaDeg;
                        break;
                    case IND_ACCELERATION:
                        if (valuesCount >= 3)
                        {
                            telAccX = ReadSingle(data, payloadOffset + 0, true);
                            telAccY = ReadSingle(data, payloadOffset + 4, true);
                            telAccZ = ReadSingle(data, payloadOffset + 8, true);
                        }
                        break;
                    case IND_COCKPIT_SHAKE:
                        if (valuesCount >= 2)
                        {
                            telCockpitShakeFreq = ReadSingle(data, payloadOffset + 0, true);
                            telCockpitShakeAmp = ReadSingle(data, payloadOffset + 4, true);
                        }
                        break;
                    case IND_AGL:
                        telAgl = valuesCount >= 1 ? ReadSingle(data, payloadOffset, true) : telAgl;
                        break;
                    case IND_FLAPS:
                        telFlaps = valuesCount >= 1 ? ReadSingle(data, payloadOffset, true) : telFlaps;
                        break;
                    case IND_AIR_BRAKES:
                        telAirBrakes = valuesCount >= 1 ? ReadSingle(data, payloadOffset, true) : telAirBrakes;
                        break;
                }

                offset = payloadOffset + payloadSize;
            }

            if (offset >= data.Length)
                return;

            byte eventsCount = data[offset++];
            for (int i = 0; i < eventsCount; i++)
            {
                if (offset + 3 > data.Length)
                    return;

                ushort eventId = ReadUInt16LE(data, offset);
                byte eventSize = data[offset + 2];
                int payloadOffset = offset + 3;
                if (payloadOffset + eventSize > data.Length)
                    return;

                switch (eventId)
                {
                    case EVT_SET_FOCUS:
                        gunSetups.Clear();
                        break;
                    case EVT_SETUP_GUN:
                        ParseSetupGunLocked(data, payloadOffset, eventSize);
                        break;
                    case EVT_DROP_BOMB:
                        ParseDropLikeEventLocked(data, payloadOffset, eventSize, isRocket: false);
                        break;
                    case EVT_ROCKET_LAUNCH:
                        ParseDropLikeEventLocked(data, payloadOffset, eventSize, isRocket: true);
                        break;
                    case EVT_HIT:
                        ParseHitLikeEventLocked(data, payloadOffset, eventSize, isDamage: false);
                        break;
                    case EVT_DAMAGE:
                        ParseHitLikeEventLocked(data, payloadOffset, eventSize, isDamage: true);
                        break;
                    case EVT_EXPLOSION:
                        ParseExplosionEventLocked(data, payloadOffset, eventSize);
                        break;
                    case EVT_GUN_FIRE:
                        ParseGunFireEventLocked(data, payloadOffset, eventSize);
                        break;
                }

                offset = payloadOffset + eventSize;
            }
        }

        private void ParseSetupGunLocked(byte[] data, int payloadOffset, int eventSize)
        {
            if (eventSize < 22)
                return;

            short index = ReadInt16LE(data, payloadOffset + 0);
            float projectileMass = ReadSingle(data, payloadOffset + 14, true);
            float shootVelocity = ReadSingle(data, payloadOffset + 18, true);

            gunSetups[index] = new GunSetup
            {
                Index = index,
                ProjectileMass = projectileMass,
                ShootVelocity = shootVelocity
            };
        }

        private void ParseGunFireEventLocked(byte[] data, int payloadOffset, int eventSize)
        {
            if (eventSize < 1)
                return;

            short gunIndex = data[payloadOffset];
            float lightImpulse = 0.06f;
            float heavyImpulse = 0f;

            GunSetup setup;
            if (gunSetups.TryGetValue(gunIndex, out setup))
            {
                float momentum = Math.Abs(setup.ProjectileMass * setup.ShootVelocity);
                bool heavy = setup.ProjectileMass >= 0.05f || momentum >= 35f;
                float baseImpulse = Clamp01(momentum / 120f);
                if (heavy)
                {
                    heavyImpulse = Math.Max(0.12f, baseImpulse);
                    lightImpulse = 0f;
                }
                else
                {
                    lightImpulse = Math.Max(0.04f, baseImpulse * 0.45f);
                }
            }

            telGunFireLight = Clamp01(telGunFireLight + lightImpulse);
            telGunFireHeavy = Clamp01(telGunFireHeavy + heavyImpulse);
        }

        private void ParseHitLikeEventLocked(byte[] data, int payloadOffset, int eventSize, bool isDamage)
        {
            if (eventSize < 24)
                return;

            float fx = ReadSingle(data, payloadOffset + 12, true);
            float fy = ReadSingle(data, payloadOffset + 16, true);
            float fz = ReadSingle(data, payloadOffset + 20, true);
            float forceMag = (float)Math.Sqrt((fx * fx) + (fy * fy) + (fz * fz));
            float impulse = Clamp01(forceMag / (isDamage ? 6500f : 5000f));
            impulse = Math.Max(0.10f, impulse);

            if (isDamage)
                telDamageShock = Clamp01(telDamageShock + impulse);
            else
                telHitShock = Clamp01(telHitShock + impulse);
        }

        private void ParseExplosionEventLocked(byte[] data, int payloadOffset, int eventSize)
        {
            if (eventSize < 16)
                return;

            float radius = ReadSingle(data, payloadOffset + 12, true);
            float impulse = Clamp01(radius / 30f);
            telExplosionShock = Clamp01(telExplosionShock + Math.Max(0.15f, impulse));
        }

        private void ParseDropLikeEventLocked(byte[] data, int payloadOffset, int eventSize, bool isRocket)
        {
            if (eventSize < 16)
                return;

            float mass = ReadSingle(data, payloadOffset + 12, true);
            float impulse;

            if (isRocket)
            {
                impulse = Clamp01(Math.Abs(mass) / 50f);
                telRocketLaunchKick = Clamp01(telRocketLaunchKick + Math.Max(0.10f, impulse));
            }
            else
            {
                impulse = Clamp01(Math.Abs(mass) / 250f);
                telBombDropKick = Clamp01(telBombDropKick + Math.Max(0.10f, impulse));
            }
        }

        private void ApplyDecayLocked(DateTime now)
        {
            if (lastDecayTime == DateTime.MinValue)
            {
                lastDecayTime = now;
                return;
            }

            double dt = (now - lastDecayTime).TotalSeconds;
            if (dt <= 0)
                return;
            if (dt > 0.25)
                dt = 0.25;

            lastDecayTime = now;

            // Dynamic motion channels decay during pauses / timeouts.
            // If live motion arrives, ParseMotionPacketLocked overwrites them on the next packet.
            float dynamicDecay = ExpDecayFactor(DYNAMIC_PAUSE_DECAY_LAMBDA, (float)dt);
            dispSpinX *= dynamicDecay;
            dispSpinY *= dynamicDecay;
            dispSpinZ *= dynamicDecay;
            dispAccX *= dynamicDecay;
            dispAccY *= dynamicDecay;
            dispAccZ *= dynamicDecay;
            dispYawDelta *= dynamicDecay;

            // Event envelopes always decay.
            telGunFireLight *= ExpDecayFactor(GUN_FIRE_DECAY_LAMBDA, (float)dt);
            telGunFireHeavy *= ExpDecayFactor(GUN_FIRE_DECAY_LAMBDA, (float)dt);
            telHitShock *= ExpDecayFactor(HIT_DECAY_LAMBDA, (float)dt);
            telDamageShock *= ExpDecayFactor(DAMAGE_DECAY_LAMBDA, (float)dt);
            telExplosionShock *= ExpDecayFactor(EXPLOSION_DECAY_LAMBDA, (float)dt);
            telBombDropKick *= ExpDecayFactor(BOMB_DROP_DECAY_LAMBDA, (float)dt);
            telRocketLaunchKick *= ExpDecayFactor(ROCKET_LAUNCH_DECAY_LAMBDA, (float)dt);
        }

        private void ReturnToHomeLocked()
        {
            if (returnedHome)
                return;

            returnedHome = true;

            float startYaw = dispYawIntegrated;
            float startPitch = dispPitchSoft;
            float startRoll = dispRollProcessed;

            for (int i = 1; i <= HOME_STEPS; i++)
            {
                float t = (float)i / HOME_STEPS;
                float eased = t * t * (3f - 2f * t);

                dispYaw = 0f;
                dispPitch = Lerp(startPitch, 0f, eased);
                dispRoll = Lerp(startRoll, 0f, eased);
                dispSpinX = 0f;
                dispSpinY = 0f;
                dispSpinZ = 0f;
                dispAccX = 0f;
                dispAccY = 0f;
                dispAccZ = 0f;
                dispYawDelta = 0f;
                dispRollProcessed = Lerp(startRoll, 0f, eased);
                dispPitchSoft = Lerp(startPitch, 0f, eased);
                dispYawIntegrated = Lerp(startYaw, 0f, eased);
                integratedYaw = dispYawIntegrated;

                telGunFireLight = 0f;
                telGunFireHeavy = 0f;
                telHitShock = 0f;
                telDamageShock = 0f;
                telExplosionShock = 0f;
                telBombDropKick = 0f;
                telRocketLaunchKick = 0f;

                PushInputsLocked();
                Thread.Sleep(HOME_STEP_MS);
            }

            dispYaw = 0f;
            dispPitch = 0f;
            dispRoll = 0f;
            dispSpinX = 0f;
            dispSpinY = 0f;
            dispSpinZ = 0f;
            dispAccX = 0f;
            dispAccY = 0f;
            dispAccZ = 0f;
            dispYawDelta = 0f;
            dispRollProcessed = 0f;
            dispPitchSoft = 0f;
            dispYawIntegrated = 0f;
            integratedYaw = 0f;

            telEngRpmAvg = 0f;
            telEngMpAvg = 0f;
            telEngShakeFreq = 0f;
            telEngShakeAmp = 0f;
            telCockpitShakeFreq = 0f;
            telCockpitShakeAmp = 0f;
            telAccX = 0f;
            telAccY = 0f;
            telAccZ = 0f;
            telEas = 0f;
            telAoaDeg = 0f;
            telAgl = 0f;
            telFlaps = 0f;
            telAirBrakes = 0f;
            telLGearPressAvg = 0f;
            telGunFireLight = 0f;
            telGunFireHeavy = 0f;
            telHitShock = 0f;
            telDamageShock = 0f;
            telExplosionShock = 0f;
            telBombDropKick = 0f;
            telRocketLaunchKick = 0f;

            PushInputsLocked();
        }

        private void PushInputsLocked()
        {
            controller.SetInput(0, dispYaw);
            controller.SetInput(1, dispPitch);
            controller.SetInput(2, dispRoll);
            controller.SetInput(3, dispSpinX);
            controller.SetInput(4, dispSpinY);
            controller.SetInput(5, dispSpinZ);
            controller.SetInput(6, dispAccX);
            controller.SetInput(7, dispAccY);
            controller.SetInput(8, dispAccZ);
            controller.SetInput(9, dispYawDelta);
            controller.SetInput(10, dispSpinZ);
            controller.SetInput(11, dispRollProcessed);
            controller.SetInput(12, dispPitchSoft);
            controller.SetInput(13, dispYawIntegrated);
            controller.SetInput(14, telEngRpmAvg);
            controller.SetInput(15, telEngMpAvg);
            controller.SetInput(16, telEngShakeFreq);
            controller.SetInput(17, telEngShakeAmp);
            controller.SetInput(18, telCockpitShakeFreq);
            controller.SetInput(19, telCockpitShakeAmp);
            controller.SetInput(20, telAccX);
            controller.SetInput(21, telAccY);
            controller.SetInput(22, telAccZ);
            controller.SetInput(23, telEas);
            controller.SetInput(24, telAoaDeg);
            controller.SetInput(25, telAgl);
            controller.SetInput(26, telFlaps);
            controller.SetInput(27, telAirBrakes);
            controller.SetInput(28, telLGearPressAvg);
            controller.SetInput(29, telGunFireLight);
            controller.SetInput(30, telGunFireHeavy);
            controller.SetInput(31, telHitShock);
            controller.SetInput(32, telDamageShock);
            controller.SetInput(33, telExplosionShock);
            controller.SetInput(34, telBombDropKick);
            controller.SetInput(35, telRocketLaunchKick);
        }

        private static float ReadAverageFloatArray(byte[] data, int offset, int count)
        {
            if (count <= 0)
                return 0f;

            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += ReadSingle(data, offset + (i * 4), true);

            return (float)(sum / count);
        }

        private static float ExpDecayFactor(float lambda, float dt)
        {
            if (lambda <= 0f || dt <= 0f)
                return 1f;
            return (float)Math.Exp(-lambda * dt);
        }

        private static float SoftLimit(float value, float hardLimit, float softZone = 0.65f)
        {
            float softStart = hardLimit * softZone;
            float absVal = Math.Abs(value);
            if (absVal <= softStart)
                return value;

            float sign = Math.Sign(value);
            float excess = absVal - softStart;
            float remaining = hardLimit - softStart;
            return sign * (softStart + remaining * (float)Math.Tanh(excess / remaining));
        }

        private static float SoftLimitAsymmetric(float value, float posLimit, float negLimit, float softZone = 0.65f)
        {
            return SoftLimit(value, value >= 0f ? posLimit : negLimit, softZone);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + ((b - a) * Clamp01(t));
        }

        private static float Clamp01(float t)
        {
            return t < 0f ? 0f : (t > 1f ? 1f : t);
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        private static ushort ReadUInt16LE(byte[] data, int offset)
        {
            return BitConverter.ToUInt16(data, offset);
        }

        private static short ReadInt16LE(byte[] data, int offset)
        {
            return BitConverter.ToInt16(data, offset);
        }

        private static uint ReadUInt32LE(byte[] data, int offset)
        {
            return BitConverter.ToUInt32(data, offset);
        }

        public static float ReadSingle(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian != littleEndian)
            {
                byte b0 = data[offset];
                data[offset] = data[offset + 3];
                data[offset + 3] = b0;
                byte b1 = data[offset + 1];
                data[offset + 1] = data[offset + 2];
                data[offset + 2] = b1;
            }
            return BitConverter.ToSingle(data, offset);
        }

        public void PatchGame() { }

        public Dictionary<string, ParameterInfo[]> GetFeatures() => null;

        public Type GetConfigBody() => typeof(Config);

        private Stream GetStream(string resourceName)
        {
            Assembly asm = GetType().Assembly;
            return asm.GetManifestResourceStream(asm.GetName().Name + ".Resources." + resourceName);
        }
    }
}
