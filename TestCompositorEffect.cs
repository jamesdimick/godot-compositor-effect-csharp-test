using System;
using Godot;
using Godot.Collections;

namespace JD
{
	/*
	 * This is a rendering effect implementation that adds a god ray type
	 * post processing effect to our rendering pipeline that is loosly based
	 * on https://twitter.com/HarryAlisavakis/status/1405807665608015872?s=20
	 *
	 * It applies the effect in 4 stages:
	 * 1 - Renders a sun disk to a new texture but taking depth into account
	 * 2 - Applies a radial blur to this image
	 * 3 - Applies a guassian blur to this image
	 * 4 - Overlays the result onto our source image
	 *
	 * The first 3 steps are implemented as compute shaders, the last is a
	 * raster pass.
	 */
	[Tool,GlobalClass]
	public partial class TestCompositorEffect : CompositorEffect
	{
		[Export] public bool HalfSize = true;

		[ExportGroup("Sun", "Sun")]
		[Export] public Vector3 SunLocation = Vector3.Up;
		[Export] public float SunSize = 150f;
		[Export] public float SunFadeSize = 50f;

		[ExportGroup("Radial Blur", "RadialBlur")]
		[Export(PropertyHint.Range, "4,32,")] public int RadialBlurSamples = 32;
		[Export] public float RadialBlurRadius = 150f;
		[Export] public float RadialBlurEffectAmount = 0.9f;

		[ExportGroup("Guassian Blur", "GaussianBlur")]
		[Export(PropertyHint.Range, "5.0,50.0,")] public float GaussianBlurSize = 16f;

		private RenderingDevice _RD;
		private Rid _NearestSampler;
		private Rid _LinearSampler;
		private Rid _RadialBlurShader;
		private Rid _RadialBlurPipeline;
		private Rid _GaussianBlurShader;
		private Rid _GaussianBlurPipeline;
		private Rid _SundiskShader;
		private Rid _SubdiskPipeline;
		private Rid _OverlayShader;
		private Rid _OverlayPipeline;
		private StringName _Context = "RadialSkyRays";
		private StringName _Texture = "texture";
		private StringName _PongTexture = "pong_texture";

		public TestCompositorEffect()
		{
			EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;
			RenderingServer.CallOnRenderThread( new Callable( this, MethodName._InitializeCompute ) );
		}

		public override void _Notification( int what )
		{
			if ( what == NotificationPredelete )
			{
				// When this is called it should be safe to clean up our shader.
				if ( _NearestSampler.IsValid )
				{
					_RD.FreeRid( _NearestSampler );
				}

				if ( _LinearSampler.IsValid )
				{
					_RD.FreeRid( _LinearSampler );
				}

				if ( _RadialBlurShader.IsValid )
				{
					_RD.FreeRid( _RadialBlurShader );
				}

				if ( _GaussianBlurShader.IsValid )
				{
					_RD.FreeRid( _GaussianBlurShader );
				}

				if ( _SundiskShader.IsValid )
				{
					_RD.FreeRid( _SundiskShader );
				}

				if ( _OverlayShader.IsValid )
				{
					_RD.FreeRid( _OverlayShader );
				}
			}
		}

		/* ###############################################################################
		 * # Everything after this point is designed to run on our rendering thread
		 */

		private void _InitializeCompute()
		{
			_RD = RenderingServer.GetRenderingDevice();

			if ( _RD is null )
			{
				return;
			}

			// Create our samplers
			RDSamplerState samplerState = new() {
				MinFilter = RenderingDevice.SamplerFilter.Nearest,
				MagFilter = RenderingDevice.SamplerFilter.Nearest
			};
			_NearestSampler = _RD.SamplerCreate( samplerState );

			samplerState = new() {
				MinFilter = RenderingDevice.SamplerFilter.Linear,
				MagFilter = RenderingDevice.SamplerFilter.Linear
			};
			_LinearSampler = _RD.SamplerCreate( samplerState );

			// Create our shaders
			RDShaderFile shaderFile = GD.Load<RDShaderFile>( "res://Shaders/temp/make_sun_disk.glsl" );
			RDShaderSpirV shaderSpirV = shaderFile.GetSpirV();
			_SundiskShader = _RD.ShaderCreateFromSpirV( shaderSpirV );
			_SubdiskPipeline = _RD.ComputePipelineCreate( _SundiskShader );

			shaderFile = GD.Load<RDShaderFile>( "res://Shaders/temp/radial_blur.glsl" );
			shaderSpirV = shaderFile.GetSpirV();
			_RadialBlurShader = _RD.ShaderCreateFromSpirV( shaderSpirV );
			_RadialBlurPipeline = _RD.ComputePipelineCreate( _RadialBlurShader );

			shaderFile = GD.Load<RDShaderFile>( "res://Shaders/temp/gaussian_blur.glsl" );
			shaderSpirV = shaderFile.GetSpirV();
			_GaussianBlurShader = _RD.ShaderCreateFromSpirV( shaderSpirV );
			_GaussianBlurPipeline = _RD.ComputePipelineCreate( _GaussianBlurShader );

			shaderFile = GD.Load<RDShaderFile>( "res://Shaders/temp/overlay.glsl" );
			shaderSpirV = shaderFile.GetSpirV();
			_OverlayShader = _RD.ShaderCreateFromSpirV( shaderSpirV );
			_OverlayPipeline = _RD.ComputePipelineCreate( _OverlayShader );
		}

		private RDUniform _GetImageUniform( Rid image, int binding = 0 )
		{
			RDUniform uniform = new() {
				UniformType = RenderingDevice.UniformType.Image,
				Binding = binding
			};
			uniform.AddId( image );

			return uniform;
		}

		private RDUniform _GetSamplerUniform( Rid image, int binding = 0, bool linear = true )
		{
			RDUniform uniform = new() {
				UniformType = RenderingDevice.UniformType.SamplerWithTexture,
				Binding = binding
			};

			if ( linear )
			{
				uniform.AddId( _LinearSampler );
			}
			else
			{
				uniform.AddId( _NearestSampler );
			}

			uniform.AddId( image );

			return uniform;
		}

		public override void _RenderCallback( int effectCallbackType, RenderData renderData )
		{
			if ( _RD is not null && effectCallbackType == (int) EffectCallbackTypeEnum.PostTransparent )
			{
				// Get our render scene buffers object, this gives us access to our render buffers. 
				// Note that implementation differs per renderer hence the need for the cast.
				RenderSceneBuffersRD renderSceneBuffers = (RenderSceneBuffersRD) renderData.GetRenderSceneBuffers();
				RenderSceneDataRD renderSceneData = (RenderSceneDataRD) renderData.GetRenderSceneData();

				if ( renderSceneBuffers is not null && renderSceneData is not null )
				{
					// Get our internal size, this is the buffer we're upscaling
					Vector2I renderSize = renderSceneBuffers.GetInternalSize();
					Vector2I effectSize = renderSize;

					if ( effectSize.X == 0 && effectSize.Y == 0 )
					{
						return;
					}

					// Render our intermediate at half size
					if ( HalfSize )
					{
						effectSize /= 2;
					}

					// If we have buffers for this viewport, check if they are the right size
					if ( renderSceneBuffers.HasTexture( _Context, _Texture ) )
					{
						RDTextureFormat tf = renderSceneBuffers.GetTextureFormat( _Context, _Texture );

						if ( tf.Width != effectSize.X || tf.Height != effectSize.Y )
						{
							// This will clear all textures for this viewport under this context
							renderSceneBuffers.ClearContext( _Context );
						}
					}

					if ( ! renderSceneBuffers.HasTexture( _Context, _Texture ) )
					{
						uint usageBits = (uint)( RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit );
						renderSceneBuffers.CreateTexture( _Context, _Texture, RenderingDevice.DataFormat.R16Unorm, usageBits, RenderingDevice.TextureSamples.Samples1, effectSize, 1, 1, true );
						renderSceneBuffers.CreateTexture( _Context, _PongTexture, RenderingDevice.DataFormat.R16Unorm, usageBits, RenderingDevice.TextureSamples.Samples1, effectSize, 1, 1, true );
					}

					_RD.DrawCommandBeginLabel( "Radial Sky Rays", Colors.White );

					// Loop through views just in case we're doing stereo rendering. No extra cost if this is mono.
					uint viewCount = renderSceneBuffers.GetViewCount();

					for ( uint view = 0; view < viewCount; view++ )
					{
						// Get our images
						Rid colorImage = renderSceneBuffers.GetColorLayer( view );
						Rid depthImage = renderSceneBuffers.GetDepthLayer( view );
						Rid textureImage = renderSceneBuffers.GetTextureSlice( _Context, _Texture, view, 0, 1, 1 );
						Rid pongTextureImage = renderSceneBuffers.GetTextureSlice( _Context, _PongTexture, view, 0, 1, 1 );

						// Get some rendering info
						Projection projection = renderSceneData.GetViewProjection( view );
						Transform3D viewMatrix = renderSceneData.GetCamTransform().Inverse();
						Vector3 eyeOffset = renderSceneData.GetViewEyeOffset( view );

						// We don't have access to our light (yet) so we get our sun direction as an export
						float sunDist = 10000f;
						Vector3 adjSunLoc = viewMatrix * new Vector3( SunLocation.X * sunDist, SunLocation.Y * sunDist, SunLocation.Z * sunDist );
						Vector4 sunProj = projection * new Vector4( adjSunLoc.X, adjSunLoc.Y, adjSunLoc.Z, 1f );
						Vector2 sunPos = new( sunProj.X / sunProj.W, sunProj.Y / sunProj.W );
						sunPos.X += eyeOffset.X;
						sunPos.Y += eyeOffset.Y;

						/* ##############################################################
						 * # Step 1: Render our sundisk
						 */

						RDUniform uniform = _GetSamplerUniform( depthImage );
						Rid depthUniformSet = UniformSetCacheRD.GetCache( _SundiskShader, 0, new Array<RDUniform> { uniform } );

						uniform = _GetImageUniform( textureImage );
						Rid textureUniformSet = UniformSetCacheRD.GetCache( _SundiskShader, 1, new Array<RDUniform> { uniform } );

						// We don't have structures (yet) so we need to build our push constant
						// "the hard way"...
						float[] pushConstant = new float[] {
							renderSize.X,
							renderSize.Y,
							effectSize.X,
							effectSize.Y,
							sunPos.X,
							sunPos.Y,
							SunSize * ( HalfSize ? 0.5f : 1f ),
							SunFadeSize * ( HalfSize ? 0.5f : 1f )
						};
						byte[] byteArray = new byte[ pushConstant.Length * 4 ];
						Buffer.BlockCopy( pushConstant, 0, byteArray, 0, byteArray.Length );

						_RD.DrawCommandBeginLabel( "Render sundisk " + view, Colors.White );

						// Run our compute shader
						uint xGroups = (uint)( ( effectSize.X - 1 ) / 8 + 1 );
						uint yGroups = (uint)( ( effectSize.Y - 1 ) / 8 + 1 );

						long computeList = _RD.ComputeListBegin();
						_RD.ComputeListBindComputePipeline( computeList, _SubdiskPipeline );
						_RD.ComputeListBindUniformSet( computeList, depthUniformSet, 0 );
						_RD.ComputeListBindUniformSet( computeList, textureUniformSet, 1 );
						_RD.ComputeListSetPushConstant( computeList, byteArray, (uint) byteArray.Length );
						_RD.ComputeListDispatch( computeList, xGroups, yGroups, 1 );
						_RD.ComputeListEnd();

						_RD.DrawCommandEndLabel();

						/* ##############################################################
						 * # Step 2: Apply radial blur
						 */

						uniform = _GetImageUniform( textureImage );
						textureUniformSet = UniformSetCacheRD.GetCache( _RadialBlurShader, 0, new Array<RDUniform> { uniform } );

						uniform = _GetImageUniform( pongTextureImage );
						Rid pongTextureUniformSet = UniformSetCacheRD.GetCache( _RadialBlurShader, 1, new Array<RDUniform> { uniform } );

						Vector2 center = new( sunPos.X * 0.5f + 0.5f, 1f - ( sunPos.Y * 0.5f + 0.5f ) );
						center *= effectSize;

						// Update push constant
						pushConstant = new float[] {
							effectSize.X,
							effectSize.Y,
							center.X,
							center.Y,
							RadialBlurSamples,
							RadialBlurRadius * ( HalfSize ? 0.5f : 1f ),
							RadialBlurEffectAmount,
							0f
						};
						byteArray = new byte[ pushConstant.Length * 4 ];
						Buffer.BlockCopy( pushConstant, 0, byteArray, 0, byteArray.Length );

						_RD.DrawCommandBeginLabel( "Apply radial blur " + view, Colors.White );

						computeList = _RD.ComputeListBegin();
						_RD.ComputeListBindComputePipeline( computeList, _RadialBlurPipeline );
						_RD.ComputeListBindUniformSet( computeList, textureUniformSet, 0 );
						_RD.ComputeListBindUniformSet( computeList, pongTextureUniformSet, 1 );
						_RD.ComputeListSetPushConstant( computeList, byteArray, (uint) byteArray.Length );
						_RD.ComputeListDispatch( computeList, xGroups, yGroups, 1 );
						_RD.ComputeListEnd();

						_RD.DrawCommandEndLabel();

						// Swap so we know our pong image is our end result
						/*Rid swap = texture_image;
						texture_image = pong_texture_image;
						pong_texture_image = swap;*/
						( pongTextureImage, textureImage ) = ( textureImage, pongTextureImage );

						/* ##############################################################
						 * # Step 3: Apply gaussian blur
						 */

						uniform = _GetImageUniform( textureImage );
						textureUniformSet = UniformSetCacheRD.GetCache( _GaussianBlurShader, 0, new Array<RDUniform> { uniform } );

						uniform = _GetImageUniform( pongTextureImage );
						pongTextureUniformSet = UniformSetCacheRD.GetCache( _GaussianBlurShader, 1, new Array<RDUniform> { uniform } );

						// Horizontal first

						// Update push constant
						pushConstant = new float[] {
							effectSize.X,
							effectSize.Y,
							GaussianBlurSize,
							0f
						};
						byteArray = new byte[ pushConstant.Length * 4 ];
						Buffer.BlockCopy( pushConstant, 0, byteArray, 0, byteArray.Length );

						_RD.DrawCommandBeginLabel( "Apply horizontal gaussian blur " + view, Colors.White );

						computeList = _RD.ComputeListBegin();
						_RD.ComputeListBindComputePipeline( computeList, _GaussianBlurPipeline );
						_RD.ComputeListBindUniformSet( computeList, textureUniformSet, 0 );
						_RD.ComputeListBindUniformSet( computeList, pongTextureUniformSet, 1 );
						_RD.ComputeListSetPushConstant( computeList, byteArray, (uint) byteArray.Length );
						_RD.ComputeListDispatch( computeList, xGroups, yGroups, 1 );
						_RD.ComputeListEnd();

						_RD.DrawCommandEndLabel();

						// And vertical
						pushConstant = new float[] {
							effectSize.X,
							effectSize.Y,
							0f,
							GaussianBlurSize
						};
						byteArray = new byte[ pushConstant.Length * 4 ];
						Buffer.BlockCopy( pushConstant, 0, byteArray, 0, byteArray.Length );

						_RD.DrawCommandBeginLabel( "Apply vertical gaussian blur " + view, Colors.White );

						computeList = _RD.ComputeListBegin();
						_RD.ComputeListBindComputePipeline( computeList, _GaussianBlurPipeline );
						_RD.ComputeListBindUniformSet( computeList, pongTextureUniformSet, 0 );
						_RD.ComputeListBindUniformSet( computeList, textureUniformSet, 1 );
						_RD.ComputeListSetPushConstant( computeList, byteArray, (uint) byteArray.Length );
						_RD.ComputeListDispatch( computeList, xGroups, yGroups, 1 );
						_RD.ComputeListEnd();

						_RD.DrawCommandEndLabel();

						/* ##############################################################
						 * # Step 4: Overlay
						 */

						_RD.DrawCommandBeginLabel( "Overlay result " + view, Colors.White );

						uniform = _GetSamplerUniform( textureImage );
						textureUniformSet = UniformSetCacheRD.GetCache( _OverlayShader, 0, new Array<RDUniform> { uniform } );

						uniform = _GetImageUniform( colorImage );
						Rid colorUniformSet = UniformSetCacheRD.GetCache( _OverlayShader, 1, new Array<RDUniform> { uniform } );

						// Update push constant
						pushConstant = new float[] {
							renderSize.X,
							renderSize.Y,
							0f,
							0f
						};
						byteArray = new byte[ pushConstant.Length * 4 ];
						Buffer.BlockCopy( pushConstant, 0, byteArray, 0, byteArray.Length );

						// Run our compute shader
						xGroups = (uint)( ( renderSize.X - 1f ) / 8f + 1f );
						yGroups = (uint)( ( renderSize.Y - 1f ) / 8f + 1f );

						computeList = _RD.ComputeListBegin();
						_RD.ComputeListBindComputePipeline( computeList, _OverlayPipeline );
						_RD.ComputeListBindUniformSet( computeList, textureUniformSet, 0 );
						_RD.ComputeListBindUniformSet( computeList, colorUniformSet, 1 );
						_RD.ComputeListSetPushConstant( computeList, byteArray, (uint) byteArray.Length );
						_RD.ComputeListDispatch( computeList, xGroups, yGroups, 1 );
						_RD.ComputeListEnd();

						_RD.DrawCommandEndLabel();
					}

					_RD.DrawCommandEndLabel();
				}
			}
		}
	}
}
