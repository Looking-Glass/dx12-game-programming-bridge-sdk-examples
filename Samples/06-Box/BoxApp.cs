using System;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D12;
using SharpDX.D3DCompiler;
using SharpDX.DXGI;
using Resource12 = SharpDX.Direct3D12.Resource;

// --- aliases to avoid type clashes ------------------------------------------
using DxcBytecode = SharpDX.D3DCompiler.ShaderBytecode;
using D3D12Bytecode = SharpDX.Direct3D12.ShaderBytecode;
using SRVDim12 = SharpDX.Direct3D12.ShaderResourceViewDimension;
// -----------------------------------------------------------------------------

namespace DX12GameProgramming
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct TexturedVertex
    {
        public Vector3 Pos;
        public Vector2 Tex;
    }

    internal sealed class BoxApp : D3DApp
    {
        private const int DefaultComponentMapping = 0x1688;   // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING

        private RootSignature _rootSignature;
        private DescriptorHeap _srvHeap;
        private DescriptorHeap[] _descriptorHeaps;

        private Resource12 _texture;
        private Resource12 _vertexBuffer;
        private VertexBufferView _vbView;

        private D3D12Bytecode _vsByteCode;
        private D3D12Bytecode _psByteCode;
        private InputLayoutDescription _inputLayout;
        private PipelineState _pso;

        private IntPtr _bridgeWindow;   // supplied elsewhere by your framework

        public BoxApp() => MainWindowCaption = "Textured Quad";

        // ---------------------------------------------------------------------
        // INITIALISATION
        // ---------------------------------------------------------------------
        public override void Initialize(string name)
        {
            base.Initialize(name);

            CommandList.Reset(DirectCmdListAlloc, null);

            BuildRootSignature();
            BuildShadersAndInputLayout();
            BuildDescriptorHeap();
            LoadTextureAndCreateSRV();
            BuildFullscreenQuad();
            BuildPSO();

            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            FlushCommandQueue();
        }

        private void BuildRootSignature()
        {
            // descriptor table: t0 (texture)
            var range = new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0);
            var rootParams = new[] { new RootParameter(ShaderVisibility.Pixel, range) };

            // static sampler that the PS expects at s0
            var staticSamplers = new[]
            {
        new StaticSamplerDescription
        {
            Filter             = Filter.MinMagMipLinear,
            AddressU           = TextureAddressMode.Clamp,
            AddressV           = TextureAddressMode.Clamp,
            AddressW           = TextureAddressMode.Clamp,
            ShaderRegister     = 0,              // s0
            RegisterSpace      = 0,
            ShaderVisibility   = ShaderVisibility.Pixel
        }
    };

            var desc = new RootSignatureDescription(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams,
                staticSamplers);

            _rootSignature = Device.CreateRootSignature(desc.Serialize());
        }


        private void BuildShadersAndInputLayout()
        {
            const string vsSrc = @"
struct VSIn  { float3 Pos:POSITION; float2 Tex:TEXCOORD; };
struct VSOut { float4 Pos:SV_POSITION; float2 Tex:TEXCOORD; };
VSOut main(VSIn v){ VSOut o; o.Pos=float4(v.Pos,1); o.Tex=v.Tex; return o; }";

            const string psSrc = @"
Texture2D tex0:register(t0);
SamplerState samp:register(s0);
struct PSIn { float4 Pos:SV_POSITION; float2 Tex:TEXCOORD; };
float4 main(PSIn i):SV_TARGET{ return tex0.Sample(samp,i.Tex); }";

            var vsBlob = DxcBytecode.Compile(vsSrc, "main", "vs_5_0");
            _vsByteCode = new D3D12Bytecode(vsBlob);

            var psBlob = DxcBytecode.Compile(psSrc, "main", "ps_5_0");
            _psByteCode = new D3D12Bytecode(psBlob);

            _inputLayout = new InputLayoutDescription(new[]
            {
                new InputElement("POSITION",0,Format.R32G32B32_Float,0,0),
                new InputElement("TEXCOORD",0,Format.R32G32_Float,   12,0)
            });
        }

        private void BuildDescriptorHeap()
        {
            _srvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                DescriptorCount = 1,
                Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                Flags = DescriptorHeapFlags.ShaderVisible
            });

            _descriptorHeaps = new[] { _srvHeap };
        }

        private void LoadTextureAndCreateSRV()
        {
            _texture = TextureUtilities.CreateTextureFromBitmap(
                Device, CommandList, @"C:\Users\zinsl\Downloads\38060_rgbd.jpg");

            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = DefaultComponentMapping,
                Format = Format.R8G8B8A8_UNorm,
                Dimension = SRVDim12.Texture2D,
                Texture2D = { MipLevels = 1 }
            };

            Device.CreateShaderResourceView(_texture, srvDesc, _srvHeap.CPUDescriptorHandleForHeapStart);
            BridgeSDK.Controller.RegisterTextureDX(_bridge_window, _texture.NativePointer);
        }

        private void BuildFullscreenQuad()
        {
            var verts = new[]
            {
        new TexturedVertex{Pos=new Vector3(-1, 1,0), Tex=new Vector2(0,0)},
        new TexturedVertex{Pos=new Vector3( 1, 1,0), Tex=new Vector2(1,0)},
        new TexturedVertex{Pos=new Vector3(-1,-1,0), Tex=new Vector2(0,1)},
        new TexturedVertex{Pos=new Vector3(-1,-1,0), Tex=new Vector2(0,1)},
        new TexturedVertex{Pos=new Vector3( 1, 1,0), Tex=new Vector2(1,0)},
        new TexturedVertex{Pos=new Vector3( 1,-1,0), Tex=new Vector2(1,1)}
    };

            int vbSize = Utilities.SizeOf<TexturedVertex>() * verts.Length;

            _vertexBuffer = Device.CreateCommittedResource(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer(vbSize),
                ResourceStates.GenericRead);

            IntPtr dataBegin = _vertexBuffer.Map(0);
            Utilities.Write(dataBegin, verts, 0, verts.Length);
            _vertexBuffer.Unmap(0);

            _vbView = new VertexBufferView
            {
                BufferLocation = _vertexBuffer.GPUVirtualAddress,
                StrideInBytes = Utilities.SizeOf<TexturedVertex>(),
                SizeInBytes = vbSize
            };
        }


        private void BuildPSO()
        {
            var psoDesc = new GraphicsPipelineStateDescription
            {
                InputLayout = _inputLayout,
                RootSignature = _rootSignature,
                VertexShader = _vsByteCode,
                PixelShader = _psByteCode,
                RasterizerState = RasterizerStateDescription.Default(),
                BlendState = BlendStateDescription.Default(),
                DepthStencilState = new DepthStencilStateDescription
                {
                    IsDepthEnabled = false,
                    DepthWriteMask = DepthWriteMask.Zero,
                    DepthComparison = Comparison.Always
                },
                SampleMask = int.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetCount = 1,
                SampleDescription = new SampleDescription(MsaaCount, MsaaQuality), // *** match swap-chain MSAA ***
                DepthStencilFormat = Format.Unknown
            };
            psoDesc.RenderTargetFormats[0] = BackBufferFormat;

            _pso = Device.CreateGraphicsPipelineState(psoDesc);
        }

        // ---------------------------------------------------------------------
        // PER-FRAME DRAW
        // ---------------------------------------------------------------------
        protected override void Draw(GameTimer gt)
        {
            DirectCmdListAlloc.Reset();
            CommandList.Reset(DirectCmdListAlloc, _pso);


            CommandList.SetViewport(Viewport);
            CommandList.SetScissorRectangles(ScissorRectangle);

            CommandList.ResourceBarrierTransition(CurrentBackBuffer,
                                                  ResourceStates.Present,
                                                  ResourceStates.RenderTarget);

            CommandList.ClearRenderTargetView(CurrentBackBufferView, Color.Black);
            CommandList.SetRenderTargets(CurrentBackBufferView, null);

            CommandList.SetDescriptorHeaps(_descriptorHeaps.Length, _descriptorHeaps);
            CommandList.SetGraphicsRootSignature(_rootSignature);
            CommandList.SetGraphicsRootDescriptorTable(0, _srvHeap.GPUDescriptorHandleForHeapStart);

            CommandList.PrimitiveTopology = PrimitiveTopology.TriangleList;
            CommandList.SetVertexBuffers(0, new[] { _vbView });
            CommandList.DrawInstanced(6, 1, 0, 0);

            CommandList.ResourceBarrierTransition(CurrentBackBuffer,
                                                  ResourceStates.RenderTarget,
                                                  ResourceStates.Present);
            CommandList.Close();
            CommandQueue.ExecuteCommandList(CommandList);
            FlushCommandQueue();


            BridgeSDK.Controller.DrawInteropRGBDTextureDX(
                _bridge_window, _texture.NativePointer,
                (uint)_texture.Description.Width,
                (uint)_texture.Description.Height,
                quiltWidth: 4096, quiltHeight: 4096, vx: 10, vy: 10, focus: 0.0f, offset: 1.0f, aspect: 0.75f, zoom: 1, depth_loc: 2);

            SwapChain.Present(0, PresentFlags.None);
            FlushCommandQueue();
        }

        // ---------------------------------------------------------------------
        // CLEAN-UP
        // ---------------------------------------------------------------------
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _vertexBuffer?.Dispose();
                _texture?.Dispose();
                _srvHeap?.Dispose();
                _pso?.Dispose();
                _rootSignature?.Dispose();
                BridgeSDK.Controller.UnregisterTextureDX(_bridge_window, _texture.NativePointer);
            }
            base.Dispose(disposing);
        }
    }
}
