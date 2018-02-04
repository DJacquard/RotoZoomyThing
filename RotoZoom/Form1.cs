using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RotoZoom
{
    public partial class Form1 : Form
    {
        private Stopwatch _stopWatch = new Stopwatch();
        private long _nextFrameTime;
        private long _frameTime;

        private long _frameCount;
        private long _fpsStartTick;
        private float _fps;

        private Bitmap _source;
        private Bitmap _target;
        private Bitmap _target1;
        private Bitmap _target2;

        int zoomFactor = 10000;
        bool direction = false;

        int xOffset = 0;
        bool xDir = false;

        byte rOffset = 0;
        byte gOffset = 0;
        int angle;
        readonly int _sourceWidth;
        readonly int _sourceHeight;

        readonly int _targetWidth;
        readonly int _targetHeight;

        readonly int[] cosTable = new int[91];
        readonly int[] sinTable = new int[91];
        private readonly ParallelOptions _parallelOptions;
        private readonly SpinWait _threadSpinWait;
        private readonly int[] jCosAngles;
        private readonly int[] jSinAngles;
        private readonly long _frequency;

        readonly ManualResetEventSlim[] _syncEvent = new ManualResetEventSlim[3];

        public Form1()
        {
            InitializeComponent();

            _source = new Bitmap(pictureBox2.Image);

            var size = _source.Size;

            var pixelFormat = _source.PixelFormat;

            _target1 = new Bitmap(pictureBox1.Width, pictureBox1.Height, _source.PixelFormat);
            _target2 = new Bitmap(pictureBox1.Width, pictureBox1.Height, _source.PixelFormat);

            _target = _target1;

            _sourceWidth = pictureBox2.Width;
            _sourceHeight = pictureBox2.Height;

            _targetWidth = pictureBox1.Width;
            _targetHeight = pictureBox1.Height;

            for (int i = 0; i < 91; ++i)
            {
                cosTable[i] = (int)(Math.Cos(Math.PI * i / 180) * 8192);
                sinTable[i] = (int)(Math.Sin(Math.PI * i / 180) * 8192);
            }

            _frameTime = Stopwatch.Frequency / 620;

            _nextFrameTime = _frameTime;

            Application.Idle += Application_Idle;

            _parallelOptions = new ParallelOptions() { MaxDegreeOfParallelism = 2 };

            _threadSpinWait = new SpinWait();

            jCosAngles = new int[_targetWidth];
            jSinAngles = new int[_targetWidth];

            _frequency = Stopwatch.Frequency;

            for(int i = 0; i < 3; ++i)
            {
                _syncEvent[i] = new ManualResetEventSlim(false);
            }

            _stopWatch.Start();
        }

        private void Application_Idle(object sender, EventArgs e)
        {
            MessageLoop();
        }

        int Cos(int deg)
        {
            if (deg <= 90)
            {
                return cosTable[deg];
            }
            else if (deg < 180)
            {
                return -cosTable[180 - deg];
            }
            else if (deg < 270)
            {
                return -cosTable[deg - 180];
            }
            else
            {
                return cosTable[360 - deg];
            }
        }

        int Sin(int deg)
        {
            if (deg <= 90)
            {
                return sinTable[deg];
            }
            else if (deg < 180)
            {
                return sinTable[180 - deg];
            }
            else if (deg < 270)
            {
                return -sinTable[deg - 180];
            }
            else
            {
                return -sinTable[360 - deg];
            }
        }

        unsafe private void CopyBitmap(int zoomFactor, int xOffset)
        {
            var sourceData = _source.LockBits(new Rectangle(0, 0, _sourceWidth, _sourceHeight), ImageLockMode.ReadOnly, _source.PixelFormat);
            var targetData = _target.LockBits(new Rectangle(0, 0, _targetWidth, _targetHeight), ImageLockMode.WriteOnly, _target.PixelFormat);

            var pixelFormatSize = Image.GetPixelFormatSize(_source.PixelFormat);

            var stride = _sourceWidth * pixelFormatSize;

            var padding = 32 - (stride % 32);

            if (padding < 32) stride += padding;

            stride /= 32;

            var targetStride = _targetWidth * pixelFormatSize;

            padding = 32 - (targetStride % 32);

            if (padding < 32) targetStride += padding;

            targetStride /= 32;

            var scan0 = (uint*)sourceData.Scan0.ToPointer();
            var targetScan0 = (uint*)targetData.Scan0.ToPointer();

            int cosAngle = Cos(angle);
            int sinAngle = Sin(angle);

            int threads = 4;

            for(int i = 0; i < threads - 1; ++i)
            {
                _syncEvent[i].Reset();

                int k = i;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    DoRender(k + 1, _targetHeight - (threads - 1 - k), threads, cosAngle, sinAngle, zoomFactor, xOffset, stride, targetStride, scan0, targetScan0
                        , jCosAngles, jSinAngles);
                    _syncEvent[k].Set();
                });
            }

            DoRender(0, _targetHeight-(threads - 1), threads, cosAngle, sinAngle, zoomFactor, xOffset, stride, targetStride, scan0, targetScan0,
                jCosAngles, jSinAngles);

            for(int i = 0; i < threads - 1; ++i)
            {
                _syncEvent[i].Wait();
            }

            _target.UnlockBits(targetData);
            _source.UnlockBits(sourceData);
        }

        private unsafe void DoRender(int start, int end, int iStep, int cosAngle, int sinAngle, int zoomFactor, int xOffset, int stride, int targetStride, uint* scan0, uint* targetScan0
            , int[] jCosAngles, int[] jSinAngles)
        {
            cosAngle = cosAngle * 1000 / zoomFactor;
            sinAngle = sinAngle * 1000 / zoomFactor;

            for (var i = start; i < end; i += iStep)
            {
                var iTargetStride = i * targetStride;

                uint* iTargetScanStride = targetScan0 + iTargetStride;

                int iSinAngle = i * sinAngle;
                int iCosAngle = i * cosAngle;

                for (var j = 0; j < _targetWidth; ++j)
                {
                    int sourceI, sourceJ;
                    int rotSourceY;
                    int rotSourceX;

                    rotSourceX = (j * cosAngle - iSinAngle);
                    rotSourceY = (j * sinAngle + iCosAngle);

                    sourceI = ((rotSourceY) >> 13) % _sourceHeight;

                    if (sourceI < 0)
                    {
                        sourceI = (sourceI + _sourceHeight) * stride;
                    }
                    else
                    {
                        sourceI = sourceI * stride;
                    }

                    sourceJ = ((rotSourceX >> 13) + xOffset) % _sourceWidth;

                    if (sourceJ < 0)
                    {
                        sourceJ += _sourceWidth;
                    }
                    Blit(scan0, iTargetScanStride, j, sourceI, sourceJ);
                }
            }
        }

        private unsafe void DoRenderF(int start, int end, int iStep, int cosAngle, int sinAngle, int zoomFactor, int xOffset, int stride, int targetStride, uint* scan0, uint* targetScan0
            , int[] jCosAngles, int[] jSinAngles)
        {
            cosAngle = cosAngle * 1000 / zoomFactor;
            sinAngle = sinAngle * 1000 / zoomFactor;

            float cosAngleF = cosAngle / 8192.0f;
            float sinAngleF = sinAngle / 8192.0f;
            float sourceHeightF = _sourceHeight;
            float xOffsetF = xOffset;
            float sourceWidthF = _sourceWidth;

            for (var i = start; i < end; i += iStep)
            {
                var iTargetStride = i * targetStride;

                uint* iTargetScanStride = targetScan0 + iTargetStride;

                float iSinAngleF = i * sinAngleF;
                float iCosAngleF = i * cosAngleF;

                for (var j = 0; j < _targetWidth; ++j)
                {
                    float sourceIF, sourceJF;
                    int sourceIFi;
                    float rotSourceXF, rotSourceYF;

                    float jf = j;

                    rotSourceXF = (jf * cosAngleF - iSinAngleF);
                    rotSourceYF = (jf * sinAngleF + iCosAngleF);

                    sourceIF = rotSourceYF % sourceHeightF;

                    if (sourceIF < 0)
                    {
                        sourceIFi = ((int)(sourceIF + sourceHeightF)) * stride;
                    }
                    else
                    {
                        sourceIFi = ((int)sourceIF) * stride;
                    }

                    sourceJF = (rotSourceXF + xOffsetF) % _sourceWidth;

                    if (sourceJF < 0)
                    {
                        sourceJF += sourceWidthF;
                    }

                    Blit(scan0, iTargetScanStride, j, sourceIFi, (int)sourceJF);
                }

                //});
            }
        }
        private unsafe void Blit(uint* scan0, uint* iTargetScanStride, int j, int sourceI, int sourceJ)
        {
            var pixel = *(scan0 + sourceI + sourceJ);
            byte* colorByte = (byte*)&pixel;
            *colorByte += rOffset;
            *(colorByte + 1) += gOffset;
            *(iTargetScanStride + j) = pixel;
        }

        private void MessageLoop()
        {
            var spinWait = new SpinWait();
            while (HasNoMessages())
            {
                if (_stopWatch.ElapsedTicks >= _nextFrameTime)
                {
                    _nextFrameTime += _frameTime;
                    Tick();
                }
                else
                {
                    //spinWait.SpinOnce();
                    //spinWait.SpinOnce();
                }
            }
        }

        private void Tick()
        {
            
            CopyBitmap(zoomFactor, xOffset);

            pictureBox1.Image = _target;

            //if (ReferenceEquals(_target, _target1))
            //{
            //    _target = _target2;
            //}
            //else
            //{
            //    _target = _target1;
            //}

            ++_frameCount;
            if (_frameCount == 30)
            {
                long endTick = _stopWatch.ElapsedTicks;
                float ticks = endTick - _fpsStartTick;
                float newFps = (_frameCount * _frequency) / ticks;
                _fps = (newFps + _fps) / 2.0f;
                label1.Text = _fps.ToString();
                _fpsStartTick = endTick;
                _frameCount = 0;
            }
            //return;
            if (direction)
            {
                if (zoomFactor > 200)
                    zoomFactor -= 20;
                else
                {
                    direction = false;
                    zoomFactor += 40;
                }
            }
            else
            {
                if (zoomFactor < 8000)
                    zoomFactor += 40;
                else
                {
                    direction = true;
                    zoomFactor -= 40;
                }
            }

            if (xDir)
            {
                if (xOffset > 0) xOffset -= 2;
                else
                {
                    xDir = false;
                    xOffset += 2;
                }
            }
            else
            {
                if (xOffset < 300) xOffset += 2;
                else
                {
                    xDir = true;
                    xOffset -= 2;
                }
            }

            angle += 1;

            if (angle > 359)
                angle -= 360;

            rOffset += 5;
            gOffset += 1;
        }

        bool HasNoMessages()
        {
            NativeMessage result;
            return PeekMessage(out result, IntPtr.Zero, 0, 0, 0) == 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeMessage
        {
            public IntPtr Handle;
            public uint Message;
            public IntPtr WParameter;
            public IntPtr LParameter;
            public uint Time;
            public Point Location;
        }

        [DllImport("user32.dll")]
        public static extern int PeekMessage(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    }
}
