using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Input;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D12.Device;
using Feature = SharpDX.Direct3D12.Feature;
using Point = SharpDX.Point;
using RectangleF = SharpDX.RectangleF;
using Resource = SharpDX.Direct3D12.Resource;

namespace DX12GameProgramming
{
    // TODO: There are currently following standing issues with all the samples:
    // TODO: Entering fullscreen mode will crash - https://github.com/d3dcoder/d3d12book/issues/2
    // TODO: Changing multisample settings will crash - https://github.com/d3dcoder/d3d12book/issues/3
    public class D3DApp : IDisposable
    {
        public const int NumFrameResources = 3;
        public const int SwapChainBufferCount = 2;

        private Form _window;             // Main window.
        private bool _appPaused;          // Is the application paused?
        private bool _minimized;          // Is the application minimized?
        private bool _maximized;          // Is the application maximized?
        private bool _resizing;           // Are the resize bars being dragged?
        private bool _running;            // Is the application running?

        // Set true to use 4X MSAA (§4.1.8).
        private bool _m4xMsaaState;       // 4X MSAA enabled.
        private int _m4xMsaaQuality;      // Quality level of 4X MSAA.

        private FormWindowState _lastWindowState = FormWindowState.Normal;

        private int _frameCount;
        private float _timeElapsed;

        private Factory4 _factory;
        private readonly Resource[] _swapChainBuffers = new Resource[SwapChainBufferCount];

        private AutoResetEvent _fenceEvent;

        public bool M4xMsaaState
        {
            get { return _m4xMsaaState; }
            set
            {
                if (_m4xMsaaState != value)
                {
                    _m4xMsaaState = value;

                    if (_running)
                    {
                        // Recreate the swapchain and buffers with new multisample settings.
                        CreateSwapChain();
                        OnResize();
                    }
                }
            }
        }

        protected DescriptorHeap RtvHeap { get; private set; }
        protected DescriptorHeap DsvHeap { get; private set; }

        protected int MsaaCount => M4xMsaaState ? 4 : 1;
        protected int MsaaQuality => M4xMsaaState ? _m4xMsaaQuality - 1 : 0;

        protected GameTimer Timer { get; } = new GameTimer();

        protected Device Device { get; private set; }

        protected Fence Fence { get; private set; }
        protected long CurrentFence { get; set; }

        protected int RtvDescriptorSize { get; private set; }
        protected int DsvDescriptorSize { get; private set; }
        protected int CbvSrvUavDescriptorSize { get; private set; }

        protected CommandQueue CommandQueue { get; private set; }
        protected CommandAllocator DirectCmdListAlloc { get; private set; }
        protected GraphicsCommandList CommandList { get; private set; }

        protected SwapChain3 SwapChain { get; private set; }
        protected Resource DepthStencilBuffer { get; private set; }

        protected ViewportF Viewport { get; set; }
        protected RectangleF ScissorRectangle { get; set; }

        protected string MainWindowCaption { get; set; } = "D3D12 Application";
        protected int ClientWidth { get; set; } = 1280;
        protected int ClientHeight { get; set; } = 720;

        protected float AspectRatio => (float)ClientWidth / ClientHeight;

        protected Format BackBufferFormat { get; } = Format.R8G8B8A8_UNorm;
        protected Format DepthStencilFormat { get; } = Format.D24_UNorm_S8_UInt;

        protected Resource CurrentBackBuffer => _swapChainBuffers[SwapChain.CurrentBackBufferIndex];
        protected CpuDescriptorHandle CurrentBackBufferView
            => RtvHeap.CPUDescriptorHandleForHeapStart + SwapChain.CurrentBackBufferIndex * RtvDescriptorSize;
        protected CpuDescriptorHandle DepthStencilView => DsvHeap.CPUDescriptorHandleForHeapStart;

        protected bool _lkg_display = false;
        protected BridgeSDK.Window _bridge_window;
        protected long _bridge_window_x = 0;
        protected long _bridge_window_y = 0;
        protected uint _bridge_window_width = 0;
        protected uint _bridge_window_height = 0;
        protected uint _bridge_max_texture_size = 0;
        protected uint _bridge_render_texture_width = 0;
        protected uint _bridge_render_texture_height = 0;
        protected uint _bridge_quilt_vx = 5;
        protected uint _bridge_quilt_vy = 9;
        protected uint _bridge_quilt_view_width = 0;
        protected uint _bridge_quilt_view_height = 0;
        protected Resource _quiltRenderTarget;
        protected Resource _quiltDepthStencilBuffer;
        protected DescriptorHeap _quiltRtvHeap;
        protected DescriptorHeap _quiltDsvHeap;
        protected DescriptorHeap _srvHeap;
        protected DescriptorHeap _samplerHeap;
        protected Resource _stagingResource;
        protected Resource _bridge_offscreen_texture;

        private void InitializeBridge(string name)
        {
            //if (!BridgeSDK.Controller.Initialize(name))
            //{
            //    throw new Exception("Failed to initialize bridge");
            //}

            if (!BridgeSDK.Controller.InitializeWithPath(name, "C:\\Repos\\LookingGlassBridge\\out\\build\\x64-Debug"))
            {
                throw new Exception("Failed to initialize bridge");
            }

            // mlc: instance the window
            bool window_status = BridgeSDK.Controller.InstanceWindowDX(Device.NativePointer, ref _bridge_window);

            if (window_status)
            {
                // mlc: cache the size of the bridge output so we can decide how large to make
                // the quilt views
                BridgeSDK.Controller.GetWindowDimensions(_bridge_window, ref _bridge_window_width, ref _bridge_window_height);
                BridgeSDK.Controller.GetWindowPosition(_bridge_window, ref _bridge_window_x, ref _bridge_window_y);

                // mlc: see how large we can make out render texture
                BridgeSDK.Controller.GetMaxTextureSize(_bridge_window, ref _bridge_max_texture_size);

                // mlc: dx12 puts an artifical cap on this on some cards:
                if (_bridge_max_texture_size > 16384)
                    _bridge_max_texture_size = 16384;

                // mlc: now we need to figure out how large our views and quilt will be
                uint desired_view_width = _bridge_window_width;
                uint desired_view_height = _bridge_window_height;

                uint desired_render_texture_width = desired_view_width * _bridge_quilt_vx;
                uint desired_render_texture_height = desired_view_height * _bridge_quilt_vy;

                if (desired_render_texture_width <= _bridge_max_texture_size &&
                    desired_render_texture_height <= _bridge_max_texture_size)
                {
                    // mlc: under the max size -- good to go!
                    _bridge_quilt_view_width = desired_view_width;
                    _bridge_quilt_view_height = desired_view_height;
                    _bridge_render_texture_width = desired_render_texture_width;
                    _bridge_render_texture_height = desired_render_texture_height;
                }
                else
                {
                    // mlc: the desired sizes are larger than we can support, find the dominant
                    // and scale down to fit.
                    float scalar = 0.0f;

                    if (desired_render_texture_width > desired_render_texture_height)
                    {
                        scalar = (float)_bridge_max_texture_size / (float)desired_render_texture_width;
                    }
                    else
                    {
                        scalar = (float)_bridge_max_texture_size / (float)desired_render_texture_height;
                    }

                    _bridge_quilt_view_width = (uint)((float)desired_view_width * scalar);
                    _bridge_quilt_view_height = (uint)((float)desired_view_height * scalar);
                    _bridge_render_texture_width = (uint)((float)desired_render_texture_width * scalar);
                    _bridge_render_texture_height = (uint)((float)desired_render_texture_height * scalar);
                }

                // mlc: create descriptor heap for offscreen
                var srvHeapDesc = new DescriptorHeapDescription
                {
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    Flags = DescriptorHeapFlags.ShaderVisible
                };

                _srvHeap = Device.CreateDescriptorHeap(srvHeapDesc);


                // mlc: create descriptor heap for quilt RTV
                var rtvHeapDesc = new DescriptorHeapDescription
                {
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.RenderTargetView,
                    Flags = DescriptorHeapFlags.None
                };
                _quiltRtvHeap = Device.CreateDescriptorHeap(rtvHeapDesc);

                // mlc: create descriptor heap for quilt DSV
                var dsvHeapDesc = new DescriptorHeapDescription
                {
                    DescriptorCount = 1,
                    Type = DescriptorHeapType.DepthStencilView,
                    Flags = DescriptorHeapFlags.None
                };

                _quiltDsvHeap = Device.CreateDescriptorHeap(dsvHeapDesc);

                // mlc: describe the render target
                var rtDesc = new ResourceDescription
                {
                    Dimension = ResourceDimension.Texture2D,
                    Alignment = 0,
                    Width = _bridge_render_texture_width,
                    Height = (int)_bridge_render_texture_height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = BackBufferFormat,
                    SampleDescription = new SampleDescription(1, 0),
                    Layout = TextureLayout.Unknown,
                    Flags = ResourceFlags.AllowRenderTarget | ResourceFlags.AllowSimultaneousAccess
                };

                // mlc: specify the clear color for the render target
                var clearColor = new ClearValue
                {
                    Format = BackBufferFormat,
                    Color = new SharpDX.Mathematics.Interop.RawVector4(0.0f, 0.0f, 0.0f, 1.0f) // Clear to black
                };

                // mlc: create the Render Target
                _quiltRenderTarget = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.Shared,
                    rtDesc,
                    ResourceStates.RenderTarget,
                    clearColor);

                // mlc: describe the Depth/Stencil Buffer
                var dsDesc = new ResourceDescription
                {
                    Dimension = ResourceDimension.Texture2D,
                    Alignment = 0,
                    Width = _bridge_render_texture_width,
                    Height = (int)_bridge_render_texture_height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = Format.D24_UNorm_S8_UInt, // Common depth format
                    SampleDescription = new SampleDescription(1, 0),
                    Layout = TextureLayout.Unknown,
                    Flags = ResourceFlags.AllowDepthStencil
                };

                // mlc: specify the clear value for the depth stencil buffer
                var depthClearValue = new ClearValue
                {
                    Format = Format.D24_UNorm_S8_UInt,
                    DepthStencil = new DepthStencilValue
                    {
                        Depth = 1.0f,
                        Stencil = 0
                    }
                };

                // mlc: create the Depth/Stencil Buffer
                _quiltDepthStencilBuffer = Device.CreateCommittedResource(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    dsDesc,
                    ResourceStates.DepthWrite,
                    depthClearValue);

                // mlc: create the RTV for the quilt render target
                var rtvHandle = _quiltRtvHeap.CPUDescriptorHandleForHeapStart;
                Device.CreateRenderTargetView(_quiltRenderTarget, null, rtvHandle);

                // mlc: create the DSV for the quilt depth stencil buffer
                var dsvHandle = _quiltDsvHeap.CPUDescriptorHandleForHeapStart;
                Device.CreateDepthStencilView(_quiltDepthStencilBuffer, null, dsvHandle);

                // mlc: create a readback resource to preview the render target
                var stagingDesc = ResourceDescription.Buffer(new ResourceAllocationInformation()
                {
                    SizeInBytes = _bridge_render_texture_width * _bridge_render_texture_height * 4,
                    Alignment = 0
                });

                _stagingResource = Device.CreateCommittedResource(
                        new HeapProperties(HeapType.Readback),
                        HeapFlags.None,
                        stagingDesc,
                        ResourceStates.CopyDestination);

                ClientWidth = (int)_bridge_window_width;
                ClientHeight = (int)_bridge_window_height;

                BridgeSDK.Controller.RegisterTextureDX(_bridge_window, _quiltRenderTarget.NativePointer);

                _lkg_display = true;

                SetWindowProperties();
            }
            else
            {
                throw new Exception("Failed to create bridge window");
            }
        }

        protected void SetWindowProperties()
        {
            // Remove the title bar and borders (set as popup)
            var hWnd = Process.GetCurrentProcess().MainWindowHandle;
            uint style = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);
            style &= ~NativeMethods.WS_OVERLAPPEDWINDOW;  // Remove standard window styles
            style |= NativeMethods.WS_POPUP;              // Set to popup (borderless)
            NativeMethods.SetWindowLong(hWnd, NativeMethods.GWL_STYLE, style);

            // Position the window at _bridge_window_x, _bridge_window_y and set the size
            NativeMethods.SetWindowPos(
                hWnd,
                IntPtr.Zero,  // No special window ordering
                (int)_bridge_window_x,
                (int)_bridge_window_y,
                (int)_bridge_window_width,
                (int)_bridge_window_height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_FRAMECHANGED
            );

            // Trigger OnResize to update the projection matrix
            OnResize();
        }


        public virtual void Initialize(string name = "Bridge SDK DX12 Example")
        {
#if DEBUG
            //----------------------------------------
            // 1.  Turn on the D3D-12 debug layer.
            //----------------------------------------
            SharpDX.Direct3D12.DebugInterface.Get().EnableDebugLayer();
#endif

            InitMainWindow();
            InitDirect3D();

#if DEBUG
            //----------------------------------------
            // 4.  After you create the Device, hook
            //     the D3D12 info-queue as well.
            //----------------------------------------
            SharpDX.Direct3D12.InfoQueue d3dQueue = Device.QueryInterface<SharpDX.Direct3D12.InfoQueue>();
            d3dQueue.SetBreakOnSeverity(SharpDX.Direct3D12.MessageSeverity.Corruption, true);
            d3dQueue.SetBreakOnSeverity(SharpDX.Direct3D12.MessageSeverity.Error, true);
            d3dQueue.MessageCountLimit = long.MaxValue;     // send all messages to Output
#endif



            // Do the initial resize code.
            OnResize();

            InitializeBridge(name);

            _running = true;
        }

        public void Run()
        {
            Timer.Reset();
            while (_running)
            {
                Application.DoEvents();
                Timer.Tick();
                //if (!_appPaused)
                //{
                CalculateFrameRateStats();
                Update(Timer);
                Draw(Timer);
                //}
                //else
                //{
                //    Thread.Sleep(100);
                //}
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // Implements the basic dispose pattern.
            // Ref: https://msdn.microsoft.com/en-us/library/b1yfkh5e(v=vs.110).aspx
            if (disposing)
            {
                FlushCommandQueue();

                RtvHeap?.Dispose();
                DsvHeap?.Dispose();
                SwapChain?.Dispose();
                foreach (Resource buffer in _swapChainBuffers)
                    buffer?.Dispose();
                DepthStencilBuffer?.Dispose();
                CommandList?.Dispose();
                DirectCmdListAlloc?.Dispose();
                CommandQueue?.Dispose();
                Fence?.Dispose();
                Device?.Dispose();
            }
        }

        protected virtual void OnResize()
        {
            Debug.Assert(Device != null);
            Debug.Assert(SwapChain != null);
            Debug.Assert(DirectCmdListAlloc != null);

            // Flush before changing any resources.
            FlushCommandQueue();

            CommandList.Reset(DirectCmdListAlloc, null);

            // Release the previous resources we will be recreating.
            foreach (Resource buffer in _swapChainBuffers)
                buffer?.Dispose();
            DepthStencilBuffer?.Dispose();

            // Resize the swap chain.
            SwapChain.ResizeBuffers(
                SwapChainBufferCount,
                ClientWidth, ClientHeight,
                BackBufferFormat,
                SwapChainFlags.AllowModeSwitch);

            CpuDescriptorHandle rtvHeapHandle = RtvHeap.CPUDescriptorHandleForHeapStart;
            for (int i = 0; i < SwapChainBufferCount; i++)
            {
                Resource backBuffer = SwapChain.GetBackBuffer<Resource>(i);
                _swapChainBuffers[i] = backBuffer;
                Device.CreateRenderTargetView(backBuffer, null, rtvHeapHandle);
                rtvHeapHandle += RtvDescriptorSize;
            }

            // Create the depth/stencil buffer and view.
            var depthStencilDesc = new ResourceDescription
            {
                Dimension = ResourceDimension.Texture2D,
                Alignment = 0,
                Width = ClientWidth,
                Height = ClientHeight,
                DepthOrArraySize = 1,
                MipLevels = 1,
                Format = Format.R24G8_Typeless,
                SampleDescription = new SampleDescription
                {
                    Count = MsaaCount,
                    Quality = MsaaQuality
                },
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.AllowDepthStencil
            };
            var optClear = new ClearValue
            {
                Format = DepthStencilFormat,
                DepthStencil = new DepthStencilValue
                {
                    Depth = 1.0f,
                    Stencil = 0
                }
            };
            DepthStencilBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                depthStencilDesc,
                ResourceStates.Common,
                optClear);

            var depthStencilViewDesc = new DepthStencilViewDescription
            {
                Dimension = M4xMsaaState
                    ? DepthStencilViewDimension.Texture2DMultisampled
                    : DepthStencilViewDimension.Texture2D,
                Format = DepthStencilFormat
            };
            // Create descriptor to mip level 0 of entire resource using a depth stencil format.
            CpuDescriptorHandle dsvHeapHandle = DsvHeap.CPUDescriptorHandleForHeapStart;
            Device.CreateDepthStencilView(DepthStencilBuffer, depthStencilViewDesc, dsvHeapHandle);

            // Transition the resource from its initial state to be used as a depth buffer.
            CommandList.ResourceBarrierTransition(DepthStencilBuffer, ResourceStates.Common, ResourceStates.DepthWrite);

            // Execute the resize commands.
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);

            // Wait until resize is complete.
            FlushCommandQueue();

            Viewport = new ViewportF(0, 0, ClientWidth, ClientHeight, 0.0f, 1.0f);
            ScissorRectangle = new RectangleF(0, 0, ClientWidth, ClientHeight);
        }

        protected virtual void Update(GameTimer gt) { }
        protected virtual void Draw(GameTimer gt) { }

        protected void InitMainWindow()
        {
            _window = new Form
            {
                Text = MainWindowCaption,
                Name = "D3DWndClassName",
                FormBorderStyle = FormBorderStyle.Sizable,
                ClientSize = new Size(ClientWidth, ClientHeight),
                StartPosition = FormStartPosition.CenterScreen,
                MinimumSize = new Size(200, 200)
            };

            _window.MouseDown += (sender, e) => OnMouseDown((MouseButtons)e.Button, new Point(e.X, e.Y));
            _window.MouseUp += (sender, e) => OnMouseUp((MouseButtons)e.Button, new Point(e.X, e.Y));
            _window.MouseMove += (sender, e) => OnMouseMove((MouseButtons)e.Button, new Point(e.X, e.Y));
            _window.KeyDown += (sender, e) => OnKeyDown((Keys)e.KeyCode);
            _window.KeyUp += (sender, e) => OnKeyUp((Keys)e.KeyCode);
            _window.ResizeBegin += (sender, e) =>
            {
                _appPaused = true;
                _resizing = true;
                Timer.Stop();
            };
            _window.ResizeEnd += (sender, e) =>
            {
                _appPaused = false;
                _resizing = false;
                Timer.Start();
                OnResize();
            };
            _window.Activated += (sender, e) =>
            {
                _appPaused = false;
                Timer.Start();
            };
            _window.Deactivate += (sender, e) =>
            {
                _appPaused = true;
                Timer.Stop();
            };
            _window.HandleDestroyed += (sender, e) => _running = false;
            _window.Resize += (sender, e) =>
            {
                ClientWidth = _window.ClientSize.Width;
                ClientHeight = _window.ClientSize.Height;
                // When window state changes.
                if (_window.WindowState != _lastWindowState)
                {
                    _lastWindowState = _window.WindowState;
                    if (_window.WindowState == FormWindowState.Maximized)
                    {
                        _appPaused = false;
                        _minimized = false;
                        _maximized = true;
                        OnResize();
                    }
                    else if (_window.WindowState == FormWindowState.Minimized)
                    {
                        _appPaused = true;
                        _minimized = true;
                        _maximized = false;
                    }
                    else if (_window.WindowState == FormWindowState.Normal)
                    {
                        if (_minimized) // Restoring from minimized state?
                        {
                            _appPaused = false;
                            _minimized = false;
                            OnResize();
                        }
                        else if (_maximized) // Restoring from maximized state?
                        {
                            _appPaused = false;
                            _maximized = false;
                            OnResize();
                        }
                        else if (_resizing)
                        {
                            // If user is dragging the resize bars, we do not resize
                            // the buffers here because as the user continuously
                            // drags the resize bars, a stream of WM_SIZE messages are
                            // sent to the window, and it would be pointless (and slow)
                            // to resize for each WM_SIZE message received from dragging
                            // the resize bars.  So instead, we reset after the user is
                            // done resizing the window and releases the resize bars, which
                            // sends a WM_EXITSIZEMOVE message.
                        }
                        else // API call such as SetWindowPos or mSwapChain->SetFullscreenState.
                        {
                            OnResize();
                        }
                    }
                }
                else if (!_resizing) // Resize due to snapping.
                {
                    OnResize();
                }
            };

            _window.Show();
            _window.Update();
        }

        protected virtual void OnMouseDown(MouseButtons button, Point location)
        {
            _window.Capture = true;
        }

        protected virtual void OnMouseUp(MouseButtons button, Point location)
        {
            _window.Capture = false;
        }

        protected virtual void OnMouseMove(MouseButtons button, Point location)
        {
        }

        protected virtual void OnKeyDown(Keys keyCode)
        {
        }

        protected virtual void OnKeyUp(Keys keyCode)
        {
            switch (keyCode)
            {
                case Keys.Escape:
                    _running = false;
                    break;
                case Keys.F2:
                    M4xMsaaState = !M4xMsaaState;
                    break;
            }
        }

        protected bool IsKeyDown(Keys keyCode) => Keyboard.IsKeyDown(KeyInterop.KeyFromVirtualKey((int)keyCode));

        protected void InitDirect3D()
        {
#if DEBUG
            // The Direct3D 12 debug layer may or may not be installed. It's installation can be
            // managed through settings page "Manage optional features" with a feature called
            // "Graphics Tools".
            // There may be a better solution to check for it instead of try/catch. If you happen
            // to know, please consider opening an issue or PR in the repo.
            try
            {
                DebugInterface.Get().EnableDebugLayer();
            }
            catch (SharpDXException ex) when (ex.Descriptor.NativeApiCode == "DXGI_ERROR_SDK_COMPONENT_MISSING")
            {
                Debug.WriteLine("Failed to enable debug layer. Please ensure \"Graphics Tools\" feature is enabled in Windows \"Manage optional feature\" settings page");
            }
#endif

            _factory = new Factory4();

            try
            {
                // Try to create hardware device.
                // Pass NULL to use the default adapter which is the first adapter that is enumerated by Factory.Adapters.
                // Ref: https://msdn.microsoft.com/en-us/library/windows/desktop/dn770336(v=vs.85).aspx
                Device = new Device(null, FeatureLevel.Level_11_0);
            }
            catch (SharpDXException)
            {
                // Fallback to WARP device.
                Adapter warpAdapter = _factory.GetWarpAdapter();
                Device = new Device(warpAdapter, FeatureLevel.Level_11_0);
            }

            Fence = Device.CreateFence(0, FenceFlags.None);
            _fenceEvent = new AutoResetEvent(false);

            RtvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            DsvDescriptorSize = Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
            CbvSrvUavDescriptorSize = Device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            // Check 4X MSAA quality support for our back buffer format.
            // All Direct3D 11 capable devices support 4X MSAA for all render 
            // target formats, so we only need to check quality support.

            FeatureDataMultisampleQualityLevels msQualityLevels;
            msQualityLevels.Format = BackBufferFormat;
            msQualityLevels.SampleCount = 4;
            msQualityLevels.Flags = MultisampleQualityLevelFlags.None;
            msQualityLevels.QualityLevelCount = 0;
            Debug.Assert(Device.CheckFeatureSupport(Feature.MultisampleQualityLevels, ref msQualityLevels));
            _m4xMsaaQuality = msQualityLevels.QualityLevelCount;

#if DEBUG
            LogAdapters();
#endif

            CreateCommandObjects();
            CreateSwapChain();
            CreateRtvAndDsvDescriptorHeaps();
        }

        protected void FlushCommandQueue()
        {
            // Advance the fence value to mark commands up to this fence point.
            CurrentFence++;

            // Add an instruction to the command queue to set a new fence point.  Because we
            // are on the GPU timeline, the new fence point won't be set until the GPU finishes
            // processing all the commands prior to this Signal().
            CommandQueue.Signal(Fence, CurrentFence);

            // Wait until the GPU has completed commands up to this fence point.
            if (Fence.CompletedValue < CurrentFence)
            {
                // Fire event when GPU hits current fence.
                Fence.SetEventOnCompletion(CurrentFence, _fenceEvent.SafeWaitHandle.DangerousGetHandle());

                // Wait until the GPU hits current fence event is fired.
                _fenceEvent.WaitOne();
            }
        }

        protected virtual int RtvDescriptorCount => SwapChainBufferCount;
        protected virtual int DsvDescriptorCount => 1;

        private void CreateCommandObjects()
        {
            var queueDesc = new CommandQueueDescription(CommandListType.Direct);
            CommandQueue = Device.CreateCommandQueue(queueDesc);

            DirectCmdListAlloc = Device.CreateCommandAllocator(CommandListType.Direct);

            CommandList = Device.CreateCommandList(
                0,
                CommandListType.Direct,
                DirectCmdListAlloc, // Associated command allocator.
                null);              // Initial PipelineStateObject.

            // Start off in a closed state.  This is because the first time we refer
            // to the command list we will Reset it, and it needs to be closed before
            // calling Reset.
            CommandList.Close();
        }

        private void CreateSwapChain()
        {
            // Release the previous swapchain we will be recreating.
            SwapChain?.Dispose();

            var sd = new SwapChainDescription
            {
                ModeDescription = new ModeDescription
                {
                    Width = ClientWidth,
                    Height = ClientHeight,
                    Format = BackBufferFormat,
                    RefreshRate = new Rational(60, 1),
                    Scaling = DisplayModeScaling.Unspecified,
                    ScanlineOrdering = DisplayModeScanlineOrder.Unspecified
                },
                SampleDescription = new SampleDescription
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = Usage.RenderTargetOutput,
                BufferCount = SwapChainBufferCount,
                SwapEffect = SwapEffect.FlipDiscard,
                Flags = SwapChainFlags.AllowModeSwitch,
                OutputHandle = _window.Handle,
                IsWindowed = true
            };

            using (var tempSwapChain = new SwapChain(_factory, CommandQueue, sd))
            {
                SwapChain = tempSwapChain.QueryInterface<SwapChain3>();
            }
        }

        private void CreateRtvAndDsvDescriptorHeaps()
        {
            var rtvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = RtvDescriptorCount,
                Type = DescriptorHeapType.RenderTargetView
            };
            RtvHeap = Device.CreateDescriptorHeap(rtvHeapDesc);

            var dsvHeapDesc = new DescriptorHeapDescription
            {
                DescriptorCount = DsvDescriptorCount,
                Type = DescriptorHeapType.DepthStencilView
            };
            DsvHeap = Device.CreateDescriptorHeap(dsvHeapDesc);
        }

        private void LogAdapters()
        {
            foreach (Adapter adapter in _factory.Adapters)
            {
                Debug.WriteLine($"***Adapter: {adapter.Description.Description}");
                LogAdapterOutputs(adapter);
            }
        }

        private void LogAdapterOutputs(Adapter adapter)
        {
            foreach (Output output in adapter.Outputs)
            {
                Debug.WriteLine($"***Output: {output.Description.DeviceName}");
                LogOutputDisplayModes(output, BackBufferFormat);
            }
        }

        private void LogOutputDisplayModes(Output output, Format format)
        {
            foreach (ModeDescription displayMode in output.GetDisplayModeList(format, 0))
                Debug.WriteLine($"Width = {displayMode.Width} Height = {displayMode.Height} Refresh = {displayMode.RefreshRate}");
        }

        private void CalculateFrameRateStats()
        {
            _frameCount++;

            if (Timer.TotalTime - _timeElapsed >= 1.0f)
            {
                float fps = _frameCount;
                float mspf = 1000.0f / fps;

                _window.Text = $"{MainWindowCaption}    fps: {fps}   mspf: {mspf}";

                // Reset for next average.
                _frameCount = 0;
                _timeElapsed += 1.0f;
            }
        }
    }
}
