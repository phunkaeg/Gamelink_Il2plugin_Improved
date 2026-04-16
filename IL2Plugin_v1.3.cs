
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
    [ExportMetadata("Version", "1.3")]
    internal class IL2Plugin : Game
    {
        // Motion packet constants (per IL-2 MotionDevice output doc)
        private const uint  MOTION_PACKET_ID   = 0x494C0100u;
        private const int   MOTION_PACKET_SIZE = 44;
        private const float RAD2DEG            = 57.29578f;

        // Socket / timeout
        private const int RECEIVE_TIMEOUT_MS = 250;
        private const int STREAM_TIMEOUT_MS  = 3000;

        // Pause / resume behaviour
        private const float PAUSE_DYNAMIC_DECAY = 0.75f; // per packet while paused
        private const int   RESUME_BLEND_FRAMES = 6;     // ~240ms at 25 Hz

        // Soft-limit shaping
        private const float SOFT_ZONE = 0.65f;

        // Platform limits (match your GameLink profile)
        private const float YAW_LIMIT_DEG      = 90f;
        private const float PITCH_FWD_LIMIT    = 15f;
        private const float PITCH_BWD_LIMIT    = 45f;
        private const float ROLL_LIMIT         = 15f;

        // Set to -1 if your rig's yaw direction is inverted relative to IL-2
        // Set to +1 if it already matches correctly
        private const float YAW_PROCESSED_SIGN = -1f;

        // Barrel-roll cueing
        private const float BARREL_ROLL_RATE_MIN   = 80f;   // deg/s
        private const float BARREL_ROLL_RATE_MAX   = 220f;  // deg/s
        private const float BARREL_ROLL_RATE_SCALE = 0.07f;

        // Return-to-home
        private const int HOME_STEPS   = 50;
        private const int HOME_STEP_MS = 40;

        // Channels
        private const int IDX_YAW_RAW        = 0;
        private const int IDX_PITCH_RAW      = 1;
        private const int IDX_ROLL_RAW       = 2;
        private const int IDX_VELOCITY_X     = 3;  // backward-compatible alias: actually Spin_X
        private const int IDX_VELOCITY_Y     = 4;  // backward-compatible alias: actually Spin_Y
        private const int IDX_VELOCITY_Z     = 5;  // backward-compatible alias: actually Spin_Z
        private const int IDX_ACCEL_X        = 6;
        private const int IDX_ACCEL_Y        = 7;
        private const int IDX_ACCEL_Z        = 8;
        private const int IDX_YAW_DELTA      = 9;
        private const int IDX_YAW_RATE       = 10;
        private const int IDX_ROLL_PROCESSED = 11;
        private const int IDX_PITCH_SOFT     = 12;
        private const int IDX_YAW_PROCESSED  = 13;
        private const int IDX_SPIN_X         = 14;
        private const int IDX_SPIN_Y         = 15;
        private const int IDX_SPIN_Z         = 16;
        private const int CHANNEL_COUNT      = 17;

        private static readonly int[] DynamicChannelIndices = new int[]
        {
            IDX_VELOCITY_X, IDX_VELOCITY_Y, IDX_VELOCITY_Z,
            IDX_ACCEL_X, IDX_ACCEL_Y, IDX_ACCEL_Z,
            IDX_YAW_DELTA, IDX_YAW_RATE,
            IDX_SPIN_X, IDX_SPIN_Y, IDX_SPIN_Z
        };

        private UdpClient udpClient;
        private bool stop;
        private Thread readThread;
        private IPEndPoint remote = new IPEndPoint(IPAddress.Any, 4321);
        private IMainFormDispatcher dispatcher;
        private IProfileManager controller;

        private bool hadTick;
        private uint lastTick;
        private DateTime lastPacketTime = DateTime.MinValue;
        private bool returnedHome;
        private readonly object homeLock = new object();

        private bool hadFirstFrame;
        private float lastYawRad;
        private float yawAccumDeg;

        private bool wasPaused;
        private int resumeBlendFramesRemaining;

        private readonly float[] disp = new float[CHANNEL_COUNT];
        private readonly float[] target = new float[CHANNEL_COUNT];
        private readonly float[] resumeFrom = new float[CHANNEL_COUNT];

        public int STEAM_ID => 307960;
        public string AUTHOR => "YawVR (modified)";
        public string PROCESS_NAME => string.Empty;
        public bool PATCH_AVAILABLE => false;
        public string Description => Resources.description;
        public Stream Logo => GetStream("logo.png");
        public Stream SmallLogo => GetStream("recent.png");
        public Stream Background => GetStream("wide.png");

        public LedEffect DefaultLED()
        {
            return new LedEffect((EFFECT_TYPE)3, 2, new YawColor[4]
            {
                new YawColor(66, 135, 245),
                new YawColor(80, 80, 80),
                new YawColor(128, 3, 117),
                new YawColor(110, 201, 12)
            }, 0.7f);
        }

        public List<Profile_Component> DefaultProfile()
        {
            return new List<Profile_Component>()
            {
                new Profile_Component(0, 0, 1f, 1f, 0f, false, false, -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
                new Profile_Component(1, 1, 1f, 1f, 0f, false, false, -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
                new Profile_Component(2, 2, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
                new Profile_Component(3, 2, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
                new Profile_Component(4, 1, 1f, 1f, 0f, false, true,  -1f, 1f, true, null, null, 0f, (ProfileComponentType)0, null),
            };
        }

        public string[] GetInputData()
        {
            return new string[CHANNEL_COUNT]
            {
                "Yaw",              //  0 raw absolute yaw (wrapped)
                "Pitch",            //  1 raw absolute pitch
                "Roll",             //  2 raw absolute roll
                "Velocity_X",       //  3 backward-compatible alias (actually Spin_X)
                "Velocity_Y",       //  4 backward-compatible alias (actually Spin_Y)
                "Velocity_Z",       //  5 backward-compatible alias (actually Spin_Z)
                "Acceleration_X",   //  6
                "Acceleration_Y",   //  7
                "Acceleration_Z",   //  8
                "Yaw_Delta",        //  9 frame-to-frame yaw change (wrap-safe)
                "Yaw_Rate",         // 10 explicit yaw-rate alias
                "Roll_Processed",   // 11 recommended for ROLL output
                "Pitch_Soft",       // 12 recommended for PITCH output
                "Yaw_Processed",    // 13 recommended for YAW output
                "Spin_X",           // 14 correct terminology alias
                "Spin_Y",           // 15 correct terminology alias
                "Spin_Z",           // 16 correct terminology alias
            };
        }

        public void SetReferences(IProfileManager controller, IMainFormDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.controller = controller;
        }

        public void Init()
        {
            Console.WriteLine("IL2 INIT (v1.3)");
            stop = false;
            returnedHome = false;
            hadTick = false;
            hadFirstFrame = false;
            wasPaused = false;
            resumeBlendFramesRemaining = 0;
            lastPacketTime = DateTime.UtcNow;
            yawAccumDeg = 0f;
            lastYawRad = 0f;

            Array.Clear(disp, 0, CHANNEL_COUNT);
            Array.Clear(target, 0, CHANNEL_COUNT);
            Array.Clear(resumeFrom, 0, CHANNEL_COUNT);

            udpClient = new UdpClient(dispatcher.GetConfigObject<Config>().Port);
            udpClient.Client.ReceiveTimeout = RECEIVE_TIMEOUT_MS;

            readThread = new Thread(new ThreadStart(ReadFunction));
            readThread.IsBackground = true;
            readThread.Start();
        }

        public void Exit()
        {
            stop = true;

            try
            {
                udpClient?.Close();
            }
            catch
            {
            }

            try
            {
                readThread?.Join(500);
            }
            catch
            {
            }

            ReturnToHome();

            udpClient = null;
        }

        private void ReadFunction()
        {
            while (!stop)
            {
                try
                {
                    byte[] data = udpClient.Receive(ref remote);

                    if (!IsValidMotionPacket(data))
                        continue;

                    lastPacketTime = DateTime.UtcNow;
                    returnedHome = false;

                    uint currentTick = BitConverter.ToUInt32(data, 4);
                    bool sameTick = hadTick && currentTick == lastTick;
                    lastTick = currentTick;
                    hadTick = true;

                    float yawRad   = ReadSingle(data,  8, true);
                    float pitchDeg = ReadSingle(data, 12, true) * RAD2DEG;
                    float rollDeg  = ReadSingle(data, 16, true) * RAD2DEG;
                    float spinX    = ReadSingle(data, 20, true) * RAD2DEG;
                    float spinY    = ReadSingle(data, 24, true) * RAD2DEG;
                    float spinZ    = ReadSingle(data, 28, true) * RAD2DEG;
                    float accX     = ReadSingle(data, 32, true);
                    float accY     = ReadSingle(data, 36, true);
                    float accZ     = ReadSingle(data, 40, true);
                    float yawDeg   = yawRad * RAD2DEG;

                    bool nearZeroState = IsNearZeroState(yawDeg, pitchDeg, rollDeg, spinX, spinY, spinZ, accX, accY, accZ);
                    bool isPaused = sameTick && nearZeroState;

                    if (isPaused)
                    {
                        HandlePausedFrame();
                    }
                    else
                    {
                        BuildLiveTargetFrame(yawRad, yawDeg, pitchDeg, rollDeg, spinX, spinY, spinZ, accX, accY, accZ);

                        if (wasPaused)
                        {
                            Array.Copy(disp, resumeFrom, CHANNEL_COUNT);
                            resumeBlendFramesRemaining = RESUME_BLEND_FRAMES;
                            wasPaused = false;
                        }

                        if (resumeBlendFramesRemaining > 0)
                        {
                            int blendStep = RESUME_BLEND_FRAMES - resumeBlendFramesRemaining + 1;
                            float t = blendStep / (float)RESUME_BLEND_FRAMES;
                            t = SmoothStep(t);

                            for (int i = 0; i < CHANNEL_COUNT; i++)
                                disp[i] = Lerp(resumeFrom[i], target[i], t);

                            resumeBlendFramesRemaining--;
                        }
                        else
                        {
                            Array.Copy(target, disp, CHANNEL_COUNT);
                        }
                    }

                    PushDisplayedInputs();
                }
                catch (SocketException ex)
                {
                    if (stop)
                        break;

                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        if (!returnedHome &&
                            (DateTime.UtcNow - lastPacketTime).TotalMilliseconds > STREAM_TIMEOUT_MS)
                        {
                            ReturnToHome();
                        }

                        continue;
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        private void BuildLiveTargetFrame(
            float yawRad, float yawDeg, float pitchDeg, float rollDeg,
            float spinX, float spinY, float spinZ,
            float accX, float accY, float accZ)
        {
            float yawDelta = 0f;

            if (!hadFirstFrame)
            {
                lastYawRad = yawRad;
                yawAccumDeg = 0f;
                hadFirstFrame = true;
            }
            else
            {
                yawDelta = NormalizeAngleDeltaDeg((yawRad - lastYawRad) * RAD2DEG);
                lastYawRad = yawRad;
                yawAccumDeg += yawDelta;
            }

            float yawProcessed  = SoftLimit(yawAccumDeg * YAW_PROCESSED_SIGN, YAW_LIMIT_DEG, SOFT_ZONE);
            float rollAngleSoft = SoftLimit(rollDeg, ROLL_LIMIT, SOFT_ZONE);
            float rollRateFeel  = SoftLimit(spinX * BARREL_ROLL_RATE_SCALE, ROLL_LIMIT, SOFT_ZONE);
            float barrelBlend   = Clamp01((Math.Abs(spinX) - BARREL_ROLL_RATE_MIN) / (BARREL_ROLL_RATE_MAX - BARREL_ROLL_RATE_MIN));
            float rollProcessed = Lerp(rollAngleSoft, rollRateFeel, barrelBlend);
            float pitchSoft     = SoftLimitAsymmetric(pitchDeg, PITCH_FWD_LIMIT, PITCH_BWD_LIMIT, SOFT_ZONE);

            target[IDX_YAW_RAW]        = yawDeg;
            target[IDX_PITCH_RAW]      = pitchDeg;
            target[IDX_ROLL_RAW]       = rollDeg;
            target[IDX_VELOCITY_X]     = spinX;
            target[IDX_VELOCITY_Y]     = spinY;
            target[IDX_VELOCITY_Z]     = spinZ;
            target[IDX_ACCEL_X]        = accX;
            target[IDX_ACCEL_Y]        = accY;
            target[IDX_ACCEL_Z]        = accZ;
            target[IDX_YAW_DELTA]      = yawDelta;
            target[IDX_YAW_RATE]       = spinZ;
            target[IDX_ROLL_PROCESSED] = rollProcessed;
            target[IDX_PITCH_SOFT]     = pitchSoft;
            target[IDX_YAW_PROCESSED]  = yawProcessed;
            target[IDX_SPIN_X]         = spinX;
            target[IDX_SPIN_Y]         = spinY;
            target[IDX_SPIN_Z]         = spinZ;
        }

        private void HandlePausedFrame()
        {
            if (!wasPaused)
            {
                wasPaused = true;
                resumeBlendFramesRemaining = 0;
            }

            // Hold pose channels exactly where they were at the moment pause was detected.
            // Decay dynamic channels toward zero so motion cueing settles gently.
            for (int i = 0; i < DynamicChannelIndices.Length; i++)
            {
                int idx = DynamicChannelIndices[i];
                disp[idx] *= PAUSE_DYNAMIC_DECAY;
            }

            disp[IDX_YAW_DELTA] = 0f;
        }

        private void PushDisplayedInputs()
        {
            for (int i = 0; i < CHANNEL_COUNT; i++)
                controller.SetInput(i, disp[i]);
        }

        private void ReturnToHome()
        {
            lock (homeLock)
            {
                if (returnedHome)
                    return;

                returnedHome = true;

                float[] start = new float[CHANNEL_COUNT];
                Array.Copy(disp, start, CHANNEL_COUNT);

                for (int step = 1; step <= HOME_STEPS; step++)
                {
                    float t = step / (float)HOME_STEPS;
                    t = SmoothStep(t);

                    for (int i = 0; i < CHANNEL_COUNT; i++)
                    {
                        float value = Lerp(start[i], 0f, t);
                        controller.SetInput(i, value);
                        disp[i] = value;
                    }

                    Thread.Sleep(HOME_STEP_MS);
                }

                for (int i = 0; i < CHANNEL_COUNT; i++)
                {
                    controller.SetInput(i, 0f);
                    disp[i] = 0f;
                }
            }
        }

        private static bool IsValidMotionPacket(byte[] data)
        {
            if (data == null || data.Length < MOTION_PACKET_SIZE)
                return false;

            return BitConverter.ToUInt32(data, 0) == MOTION_PACKET_ID;
        }

        private static bool IsNearZeroState(
            float yawDeg, float pitchDeg, float rollDeg,
            float spinX, float spinY, float spinZ,
            float accX, float accY, float accZ)
        {
            const float angleEps = 0.02f;
            const float spinEps  = 0.05f;
            const float accEps   = 0.05f;

            return Math.Abs(yawDeg)   < angleEps &&
                   Math.Abs(pitchDeg) < angleEps &&
                   Math.Abs(rollDeg)  < angleEps &&
                   Math.Abs(spinX)    < spinEps  &&
                   Math.Abs(spinY)    < spinEps  &&
                   Math.Abs(spinZ)    < spinEps  &&
                   Math.Abs(accX)     < accEps   &&
                   Math.Abs(accY)     < accEps   &&
                   Math.Abs(accZ)     < accEps;
        }

        private static float NormalizeAngleDeltaDeg(float delta)
        {
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;
            return delta;
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

            if (remaining <= 0.0001f)
                return sign * hardLimit;

            float compressed = softStart + remaining * (float)Math.Tanh(excess / remaining);
            return sign * compressed;
        }

        private static float SoftLimitAsymmetric(float value, float posLimit, float negLimit, float softZone = 0.65f)
        {
            float hardLimit = value >= 0f ? posLimit : negLimit;
            return SoftLimit(value, hardLimit, softZone);
        }

        private static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * Clamp01(t);
        }

        private static float Clamp01(float t)
        {
            if (t < 0f) return 0f;
            if (t > 1f) return 1f;
            return t;
        }

        private static float SmoothStep(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
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

        public void PatchGame()
        {
        }

        public Dictionary<string, System.Reflection.ParameterInfo[]> GetFeatures()
        {
            return null;
        }

        public Type GetConfigBody()
        {
            return typeof(Config);
        }

        private Stream GetStream(string resourceName)
        {
            Assembly asm = GetType().Assembly;
            return asm.GetManifestResourceStream(asm.GetName().Name + ".Resources." + resourceName);
        }
    }
}
