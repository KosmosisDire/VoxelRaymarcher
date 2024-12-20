using System.Numerics;
using Engine;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using MeshShader = Engine.MeshShader;

public class VoxelRenderer : IDisposable
{
    private readonly GLContext ctx;
    private readonly Window window;
    private readonly Camera camera;
    private readonly ChunkManager chunkManager;

    private ComputeShader renderShader;
    private ComputeShader sandSimShader;
    private ComputeShader denoiseShader;
    private ComputeShader compositeShader;

    // Split FBOs
    private uint geometryFbo;
    private uint postFbo;

    // Geometry pass textures
    private uint colorTexture;
    private uint normalTexture;
    private uint depthTexture;
    private uint aoTexture;
    private uint materialTexture;

    // Post-process textures
    private uint tempAoTexture;
    private uint denoisedAoTexture;
    private uint shadowTexture;
    private uint compositedTexture;
    private uint motionTexture;

    private Vector2D<int> currentSize;

    // Lighting parameters
    private Vector3 sunDirection = new(0.7f, 0.5f, 0.5f);
    private Vector3 sunColor = new(1f, 0.95f, 0.8f);

    // Previous frame camera data for motion vectors
    private Matrix4x4 prevViewProjectionMatrix;
    private Vector3 prevCameraPosition;

    // Sand simulation parameters
    private uint currentFrame = 0;
    private const uint PASSES = 27;

    // Screen quad for output
    private Mesh screenQuad;
    private MeshShader blitShader;

    // Generation parameters
    private const int ChunksPerFrame = 128;
    private bool isGenerating = true;
    private ulong generatedChunks = 0;

    public RaycastHit hoveredCastResult;
    public RaycastSystem raycastSystem;
    public VoxelEditorSystem voxelEditorSystem;

    public unsafe VoxelRenderer(Window window, Camera camera)
    {
        this.ctx = window.context;
        this.window = window;
        this.camera = camera;
        this.chunkManager = new ChunkManager();
        this.currentSize = window.Size;

        // Initialize previous frame data
        this.prevViewProjectionMatrix = camera.ViewMatrix * camera.PerspectiveMatrix;
        this.prevCameraPosition = camera.Position;

        // Create screen quad for output display
        screenQuad = MeshGen.Quad(ctx);
        screenQuad.CreateFlattenedBuffers();
        screenQuad.UploadBuffers();

        // Initialize shaders
        renderShader = new ComputeShader(ctx, "shaders/raymarch.comp.glsl");
        sandSimShader = new ComputeShader(ctx, "shaders/sand-sim.comp.glsl");
        denoiseShader = new ComputeShader(ctx, "shaders/denoise.comp.glsl");
        compositeShader = new ComputeShader(ctx, "shaders/composite.comp.glsl");
        blitShader = new MeshShader(ctx, "shaders/vert.glsl", "shaders/tex-frag.glsl");

        raycastSystem = new RaycastSystem(ctx, chunkManager);
        voxelEditorSystem = new VoxelEditorSystem(ctx, chunkManager);

        // Create FBO and textures
        CreateFramebufferResources();

        // Initialize chunk manager
        chunkManager.CreateBuffers(ctx);

        // Subscribe to window resize
        window.SilkWindow.Resize += HandleResize;
    }

    private void HandleResize(Vector2D<int> newSize)
    {
        if (newSize == currentSize) return;

        currentSize = newSize;
        ctx.ExecuteCmd((dt, gl) =>
        {
            DeleteFramebufferResources(gl);
            CreateFramebufferResources();
        });
    }

    private unsafe void CreateFramebufferResources()
    {
        ctx.ExecuteCmd((dt, gl) =>
        {
            // Create Geometry FBO
            gl.CreateFramebuffers(1, out geometryFbo);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, geometryFbo);

            // Create Post-process FBO
            gl.CreateFramebuffers(1, out postFbo);

            // Create geometry pass textures
            CreateTexture(gl, ref colorTexture, GLEnum.Rgba32f);
            CreateTexture(gl, ref normalTexture, GLEnum.Rgba16f);
            CreateTexture(gl, ref depthTexture, GLEnum.R32f);
            CreateTexture(gl, ref aoTexture, GLEnum.R16f);
            CreateTexture(gl, ref materialTexture, GLEnum.R8ui);

            // Create post-process textures
            CreateTexture(gl, ref tempAoTexture, GLEnum.R16f);
            CreateTexture(gl, ref denoisedAoTexture, GLEnum.R16f);
            CreateTexture(gl, ref shadowTexture, GLEnum.R16f);
            CreateTexture(gl, ref compositedTexture, GLEnum.Rgba8);
            CreateTexture(gl, ref motionTexture, GLEnum.RG16f);

            // Attach textures to geometry FBO
            gl.NamedFramebufferTexture(geometryFbo, FramebufferAttachment.ColorAttachment0, colorTexture, 0);
            gl.NamedFramebufferTexture(geometryFbo, FramebufferAttachment.ColorAttachment1, normalTexture, 0);
            gl.NamedFramebufferTexture(geometryFbo, FramebufferAttachment.ColorAttachment2, depthTexture, 0);
            gl.NamedFramebufferTexture(geometryFbo, FramebufferAttachment.ColorAttachment3, aoTexture, 0);
            gl.NamedFramebufferTexture(geometryFbo, FramebufferAttachment.ColorAttachment4, materialTexture, 0);

            // Attach textures to post FBO
            gl.NamedFramebufferTexture(postFbo, FramebufferAttachment.ColorAttachment0, tempAoTexture, 0);
            gl.NamedFramebufferTexture(postFbo, FramebufferAttachment.ColorAttachment1, denoisedAoTexture, 0);
            gl.NamedFramebufferTexture(postFbo, FramebufferAttachment.ColorAttachment2, shadowTexture, 0);
            gl.NamedFramebufferTexture(postFbo, FramebufferAttachment.ColorAttachment3, compositedTexture, 0);
            gl.NamedFramebufferTexture(postFbo, FramebufferAttachment.ColorAttachment4, motionTexture, 0);

            // Enable draw buffers for geometry FBO
            GLEnum[] geometryBuffers = {
                GLEnum.ColorAttachment0,
                GLEnum.ColorAttachment1,
                GLEnum.ColorAttachment2,
                GLEnum.ColorAttachment3,
                GLEnum.ColorAttachment4
            };
            gl.NamedFramebufferDrawBuffers(geometryFbo, 5, geometryBuffers);

            // Enable draw buffers for post FBO
            GLEnum[] postBuffers = {
                GLEnum.ColorAttachment0,
                GLEnum.ColorAttachment1,
                GLEnum.ColorAttachment2,
                GLEnum.ColorAttachment3,
                GLEnum.ColorAttachment4
            };
            gl.NamedFramebufferDrawBuffers(postFbo, 5, postBuffers);

            // Verify framebuffers are complete
            if (gl.CheckNamedFramebufferStatus(geometryFbo, FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new Exception("Geometry framebuffer is incomplete!");
            if (gl.CheckNamedFramebufferStatus(postFbo, FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new Exception("Post-process framebuffer is incomplete!");
        });
    }

    private unsafe void CreateTexture(GL gl, ref uint texture, GLEnum format)
    {
        gl.CreateTextures(TextureTarget.Texture2D, 1, out texture);
        gl.TextureStorage2D(texture, 1, format, (uint)currentSize.X, (uint)currentSize.Y);
        gl.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TextureParameter(texture, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TextureParameter(texture, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    private unsafe void DeleteFramebufferResources(GL gl)
    {
        if (geometryFbo != 0)
        {
            gl.DeleteFramebuffer(geometryFbo);
            gl.DeleteFramebuffer(postFbo);
            gl.DeleteTexture(colorTexture);
            gl.DeleteTexture(normalTexture);
            gl.DeleteTexture(depthTexture);
            gl.DeleteTexture(aoTexture);
            gl.DeleteTexture(materialTexture);
            gl.DeleteTexture(tempAoTexture);
            gl.DeleteTexture(denoisedAoTexture);
            gl.DeleteTexture(shadowTexture);
            gl.DeleteTexture(compositedTexture);
            gl.DeleteTexture(motionTexture);
        }
    }

    private void DispatchSandSimulation(GL gl, double dt)
    {
        // Set uniforms
        sandSimShader.SetUniform("deltaTime", dt);

        // Calculate dispatch size for 1/27th (3^3) of the volume
        uint totalSize = ChunkManager.ChunkSize * ChunkManager.GridSize * ChunkManager.ChunkSize;
        totalSize = (uint)Math.Ceiling(totalSize / 3f);

        // Calculate work groups based on the local size (8x8x8)
        uint groupsX = (totalSize + 7) / 8;
        uint groupsY = (totalSize + 7) / 8;
        uint groupsZ = (totalSize + 7) / 8;

        // Dispatch the compute shader
        for (int i = 0; i < 4; i++)
        {
            sandSimShader.Use();
            sandSimShader.SetUniform("frameNumber", currentFrame);
            sandSimShader.SetUniform("passIndex", currentFrame % PASSES);
            sandSimShader.Dispatch(groupsX, groupsY, groupsZ);
            currentFrame++;
        }
    }

    bool clicked = false;
    public int selectedMaterial = 1; // from 1 to ChunkManager.Materials.Length - 1
    public int placeSize = 4;

    public void Draw(double dt)
    {
        ctx.RenderCmd((dt, gl) =>
        {
            chunkManager.BindBuffers(gl);
            hoveredCastResult = raycastSystem.Raycast(camera.Position, camera.Forward).hits[0];

            Vector3 cornerPos = hoveredCastResult.cellPosition;
        
            Vector3 size = new Vector3(placeSize, placeSize, placeSize);
            cornerPos = new Vector3(
                (float)Math.Floor(cornerPos.X / size.X) * size.X,
                (float)Math.Floor(cornerPos.Y / size.Y) * size.Y,
                (float)Math.Floor(cornerPos.Z / size.Z) * size.Z
            );
            
            
            // if mouse down and raycast hit, place voxel
            if (!clicked && ctx.Input.Mice[0].IsButtonPressed(MouseButton.Right) && hoveredCastResult.valid)
            {
                cornerPos += hoveredCastResult.normal * size;
                voxelEditorSystem.PlaceVoxels(cornerPos, size, selectedMaterial);
                clicked = true;
            }

            if (!clicked && ctx.Input.Mice[0].IsButtonPressed(MouseButton.Left) && hoveredCastResult.valid)
            {
                voxelEditorSystem.ClearVoxels(cornerPos, size);
                clicked = true;
            }

            if (!ctx.Input.Mice[0].IsButtonPressed(MouseButton.Left) && !ctx.Input.Mice[0].IsButtonPressed(MouseButton.Right))
            {
                clicked = false;
            }

            // if mouse wheel is scrolled, change selected material 
            if (ctx.Input.Mice[0].ScrollWheels[0].Y != 0)
            {
                if (ctx.Input.Keyboards[0].IsKeyPressed(Key.ShiftLeft))
                {
                    // move by powers of 2
                    if ((int)ctx.Input.Mice[0].ScrollWheels[0].Y > 0)
                    {
                        placeSize *= 2;
                    }
                    else
                    {
                        placeSize /= 2;
                    }

                    if (placeSize < 1)
                    {
                        placeSize = 1;
                    }
                    if (placeSize > 1024)
                    {
                        placeSize = 1024;
                    }
                }
                else
                {
                    selectedMaterial += (int)ctx.Input.Mice[0].ScrollWheels[0].Y;
                    if (selectedMaterial < 1)
                    {
                        selectedMaterial = 1;
                    }
                    if (selectedMaterial >= Materials.materials.Length)
                    {
                        selectedMaterial = Materials.materials.Length - 1;
                    }
                }
            }
            
            // Generate chunks if needed
            if (isGenerating && generatedChunks < ChunkManager.NumChunks / 2)
            {
                chunkManager.GenerateChunkTerrain(ctx, ChunksPerFrame);
                generatedChunks += (ulong)ChunksPerFrame;

                if (generatedChunks >= ChunkManager.NumChunks / 2)
                {
                    Console.WriteLine("Finished generating chunks");
                    isGenerating = false;
                }
            }

            DispatchSandSimulation(gl, dt);

            uint workGroupsX = (uint)((currentSize.X + 15) / 16);
            uint workGroupsY = (uint)((currentSize.Y + 15) / 16);

            // Geometry pass
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, geometryFbo);
            {
                gl.BindImageTexture(0, colorTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba32f);
                gl.BindImageTexture(1, normalTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba16f);
                gl.BindImageTexture(2, depthTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R32f);
                gl.BindImageTexture(3, aoTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R16f);
                gl.BindImageTexture(4, materialTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R8ui);
                gl.BindImageTexture(5, shadowTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.R16f);
                gl.BindImageTexture(6, motionTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.RG16f);

                renderShader.Use();
                SetupRenderUniforms();
                renderShader.Dispatch(workGroupsX, workGroupsY, 1);
            }

            // Denoising passes
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, postFbo);
            {
                // Bind textures for denoising
                gl.BindImageTexture(1, normalTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.Rgba16f);
                gl.BindImageTexture(2, depthTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R32f);
                gl.BindImageTexture(3, aoTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R16f);
                gl.BindImageTexture(4, tempAoTexture, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R16f);
                gl.BindImageTexture(5, denoisedAoTexture, 0, false, 0, BufferAccessARB.ReadWrite, InternalFormat.R16f);
                gl.BindImageTexture(6, motionTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.RG16f);
                
                denoiseShader.Use();
                
                // Initial copy pass
                denoiseShader.SetUniform("pass", 0);
                denoiseShader.Dispatch(workGroupsX, workGroupsY, 1);

                // Multiple filtering passes with decreasing kernel sizes
                float[] kernelScales = new[] { 1.0f, 0.75f, 0.5f };
                foreach (float scale in kernelScales)
                {
                    // Horizontal pass
                    denoiseShader.SetUniform("pass", 1);
                    denoiseShader.SetUniform("filterSize", scale);
                    denoiseShader.Dispatch(workGroupsX, workGroupsY, 1);

                    // Vertical pass
                    denoiseShader.SetUniform("pass", 2);
                    denoiseShader.SetUniform("filterSize", scale);
                    denoiseShader.Dispatch(workGroupsX, workGroupsY, 1);
                }
            }

            // Composite pass
            {
                // Bind input textures
                gl.BindImageTexture(0, colorTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.Rgba32f);
                gl.BindImageTexture(1, normalTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.Rgba16f);
                gl.BindImageTexture(2, depthTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R32f);
                gl.BindImageTexture(3, denoisedAoTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R16f);
                gl.BindImageTexture(4, materialTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R8ui);
                gl.BindImageTexture(5, shadowTexture, 0, false, 0, BufferAccessARB.ReadOnly, InternalFormat.R16f);
                gl.BindImageTexture(6, compositedTexture, 0, false, 0, BufferAccessARB.WriteOnly, InternalFormat.Rgba8);

                compositeShader.Use();
                compositeShader.SetUniform("sunDir", sunDirection);
                compositeShader.SetUniform("sunColor", sunColor);
                compositeShader.SetUniform("cameraPos", camera.Position);
                compositeShader.SetUniform("viewMatrix", camera.ViewMatrix);
                compositeShader.SetUniform("projMatrix", camera.PerspectiveMatrix);
                compositeShader.Dispatch(workGroupsX, workGroupsY, 1);
            }

            // Final blit to screen
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            
            blitShader.Use();
            gl.BindTextureUnit(0, compositedTexture);
            blitShader.SetUniform("tex", 0);
            screenQuad.Draw();

            // Store current frame data for next frame's motion vectors
            prevViewProjectionMatrix = camera.ViewMatrix * camera.PerspectiveMatrix;
            prevCameraPosition = camera.Position;
        });
    }

    private void SetupRenderUniforms()
    {
        renderShader.SetUniform("viewMatrix", camera.ViewMatrix);
        renderShader.SetUniform("projMatrix", camera.PerspectiveMatrix);
        renderShader.SetUniform("cameraPos", camera.Position);
        renderShader.SetUniform("prevViewProjMatrix", prevViewProjectionMatrix);
        renderShader.SetUniform("prevCameraPos", prevCameraPosition);
        renderShader.SetUniform("sunDir", sunDirection);
        renderShader.SetUniform("sunColor", sunColor);
        renderShader.SetUniform("time", (float)window.SilkWindow.Time);
    }

    public void Dispose()
    {
        window.SilkWindow.Resize -= HandleResize;

        ctx.ExecuteCmd((dt, gl) =>
        {
            renderShader?.Dispose();
            sandSimShader?.Dispose();
            denoiseShader?.Dispose();
            compositeShader?.Dispose();
            blitShader?.Dispose();
            DeleteFramebufferResources(gl);
            chunkManager.Dispose(gl);
            screenQuad?.Dispose();
        });
    }
}