$GLUE_VERSIONS$

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FlatRedBall.Graphics;
using Microsoft.Xna.Framework.Graphics;
using FlatRedBall;
using Microsoft.Xna.Framework;
using FlatRedBall.Content;
using FlatRedBall.Content.Scene;
using FlatRedBall.IO;
using FlatRedBall.Input;
using FlatRedBall.Debugging;
using FlatRedBall.Math;
using TMXGlueLib.DataTypes;
using VertexType = Microsoft.Xna.Framework.Graphics.VertexPositionTexture;

namespace FlatRedBall.TileGraphics
{
    #region Enums

    public enum SortAxis
    {
        None,
        X,
        Y,
        YTopDown
    }

    #endregion

    public class MapDrawableBatch : PositionedObject, IVisible, IDrawableBatch
    {
        #region Fields
        protected Tileset mTileset;

#if RendererHasExternalEffectManager
        /// <summary>
        /// Use the custom shader instead of MG's default. This enables using 
        /// color operations and linearization for gamma correction.
        /// </summary>
        public static bool UseCustomEffect { get; set; }
#endif

        /// <summary>
        /// The effect used to draw. Shared by all instances for performance reasons
        /// </summary>
        private static BasicEffect mBasicEffect;
        private static AlphaTestEffect mAlphaTestEffect;

        /// <summary>
        /// The vertices used to draw the map.
        /// </summary>
        /// <remarks>
        /// Coordinate order is:
        /// 3   2
        ///
        /// 0   1
        /// </remarks>
        protected VertexType[] mVertices;

        protected Texture2D mTexture;

        /// <summary>
        /// The indices to draw the shape
        /// </summary>
        protected int[] indices32Bit;
        protected short[] indices16Bit;

        Dictionary<string, List<int>> mNamedTileOrderedIndexes = new Dictionary<string, List<int>>();

        public byte[] FlipFlagArray;

        private int mCurrentNumberOfTiles = 0;

        float mRed = 1;
        float mGreen = 1;
        float mBlue = 1;
        float mAlpha = 1;

        public float Red
        {
            get { return mRed; }
            set { mRed = value; }
        }

        public float Green
        {
            get { return mGreen; }
            set { mGreen = value; }
        }

        public float Blue
        {
            get { return mBlue; }
            set { mBlue = value; }
        }

        /// <summary>
        /// Modifies the transparency of the map. By default this value equals 1 which means it is drawn at full opacity. A value of 0 makes the map fully transparent.
        /// </summary>
        public float Alpha
        {
            get { return mAlpha; }
            set { mAlpha = value; }
        }

        ColorOperation mColorOperation = ColorOperation.Modulate;
        public ColorOperation ColorOperation
        {
            get { return mColorOperation; }
            set { mColorOperation = value; }
        }

        private SortAxis mSortAxis;

        #endregion

        #region Properties

        public List<TMXGlueLib.DataTypes.NamedValue> Properties
        {
            get;
            private set;
        } = new List<TMXGlueLib.DataTypes.NamedValue>();

        /// <summary>
        /// The axis on which tiles are sorted. This is used to perform tile culling for performance. 
        /// Setting this to SortAxis.None will turn off culling.
        /// </summary>
        public SortAxis SortAxis
        {
            get
            {
                return mSortAxis;
            }
            set
            {
                mSortAxis = value;
            }
        }

        /// <summary>
        /// Whether this batch
        /// updated every frame.  Since MapDrawableBatches do not
        /// have any built-in update, this value defaults to false.
        /// </summary>
        public bool UpdateEveryFrame
        {
            get { return true; }
        }

        /// <summary>
        /// Mulitplier value used to scale the rendering of the map. 
        /// A value of 1 (default) means the map is rendered at its original size.
        /// A value of 2 would render the map at twice its size.
        /// </summary>
        public float RenderingScale
        {
            get;
            set;
        }

        /// <summary>
        /// Contains the list of tile indexes for each name, enabling quick lookup of tile
        /// indexes by name. The indexes stored in the list indicate the ordered index of the tile
        /// (not the vertex or index in the index buffer), so the values will always be less
        /// than the total number of tiles in the MapDrawableBatch.
        /// </summary>
        public Dictionary<string, List<int>> NamedTileOrderedIndexes
        {
            get
            {
                return mNamedTileOrderedIndexes;
            }
        }

        public bool Visible
        {
            get;
            set;
        }

        public bool ZBuffered
        {
            get;
            set;
        }

        public int QuadCount
        {
            get
            {
                return mVertices.Length / 4;
            }
        }

        /// <summary>
        /// The vertices used to draw the map. These are typically not directly manipulated, but rather are set internally
        /// when converting from a .TMX.
        /// </summary>
        /// <remarks>
        /// Typically when dealing with the vertices, we deal with "quads"
        /// The number of vertices should be divisible by 4, where each quad
        /// is 4 vertices. Therefore, to get the bottom-left vertex of a quad
        /// by quad index, you would do:
        /// var vertexIndex = quadIndex * 4;
        /// var bottomLeft = mVertices[vertexIndex];
        /// 
        /// Vertices are in counterclockwise order, starting with the bottom-left:
        /// 3   2
        ///
        /// 0   1
        /// </remarks>
        public VertexType[] Vertices => mVertices;

        public Texture2D Texture
        {
            get
            {
                return mTexture;
            }
            set
            {
                if (value == null)
                {
                    throw new Exception("Texture can't be null.");
                }

                if (mTexture != null && (mTexture.Width != value.Width || mTexture.Height != value.Height))
                {
                    throw new Exception("New texture must match previous texture dimensions.");
                }

                mTexture = value;
            }
        }

        // Doing these properties this way lets me avoid a computational step of 1 - ParallaxMultiplier in the Update() function
        // To explain the get & set values, algebra:
        // if _parallaxMultiplier = 1 - value (set)
        // then _parallaxMultiplier - 1 = -value
        // so -(_parallaxMultiplier - 1) = value
        // thus -_parallaxMultiplier + 1 = value (get)
        private float _parallaxMultiplierX;

        /// <summary>
        /// The multiplier applied when scrolling the camera. 
        /// Defaults to a value of 1, which 
        /// means when the camera scrolls 1 unit, the 
        /// layer moves by 1 unit to the left. A value of less than 1 should be used for
        /// layers in the background, while a value greater
        /// than 1 for layers in the foreground.
        /// </summary>
        public float ParallaxMultiplierX
        {
            get => -_parallaxMultiplierX + 1;
            set => _parallaxMultiplierX = 1 - value;
        }

        private float _parallaxMultiplierY;
        /// <summary>
        /// The multiplier applied when scrolling the camera. 
        /// Defaults to a value of 1, which 
        /// means when the camera scrolls 1 unit, the 
        /// layer moves by 1 unit to the left. A value of less than 1 should be used for
        /// layers in the background, while a value greater
        /// than 1 for layers in the foreground.
        /// </summary>
        public float ParallaxMultiplierY
        {
            get { return -_parallaxMultiplierY + 1; }
            set { _parallaxMultiplierY = 1 - value; }
        }

        public TextureFilter? TextureFilter { get; set; } = null;

        #endregion

        #region Constructor / Initialization

        // this exists purely for Clone
        public MapDrawableBatch()
        {
        }

        public MapDrawableBatch(int numberOfTiles, Texture2D texture)
            : base()
        {
            if (texture == null)
                throw new ArgumentNullException("texture");

            InternalInitialize(numberOfTiles);

            mTexture = texture;

        }

        /// <summary>
        /// Create a new MapDrawableBatch with vertices to hold numberOfTiles. This creates a new Tileset which 
        /// stores the argument texture which can be used to add or paint tiles.
        /// </summary>
        /// <remarks>
        /// Although maps typically have fixed dimensions, this is not required. Tiles can be added anywhere so
        /// no dimension parameters are required.
        /// </remarks>
        public MapDrawableBatch(int numberOfTiles, int textureTileDimensionWidth, int textureTileDimensionHeight, Texture2D texture)
            : base()
        {
            // Update - maybe this is okay, it's just an empty layer, and we want to support that...
            //if (texture == null)
            //    throw new ArgumentNullException("texture");

            InternalInitialize(numberOfTiles);

            mTexture = texture;

            mTileset = new Tileset(texture, textureTileDimensionWidth, textureTileDimensionHeight);
        }

        void InternalInitialize(int numberOfTiles)
        {
            Visible = true;
            mVertices = new VertexType[4 * numberOfTiles];
            FlipFlagArray = new byte[numberOfTiles];

            var indexCount = 6 * numberOfTiles;
            if (indexCount < short.MaxValue)
            {
                indices16Bit = new short[indexCount];
            }
            else
            {
                // This is a large map, so we need to use 32 bit indices
                indices32Bit = new int[indexCount];
            }

            // We're going to share these because creating effects is slow...
            // But is this okay if we tombstone?
            if (mBasicEffect == null)
            {
                mBasicEffect = new BasicEffect(FlatRedBallServices.GraphicsDevice);

                mBasicEffect.VertexColorEnabled = false;
                mBasicEffect.TextureEnabled = true;
            }
            if (mAlphaTestEffect == null)
            {
                mAlphaTestEffect = new AlphaTestEffect(FlatRedBallServices.GraphicsDevice);
                mAlphaTestEffect.Alpha = 1;
                mAlphaTestEffect.VertexColorEnabled = false;

            }

            RenderingScale = 1;
        }

        #endregion

        #region Methods

        public void AddToManagers()
        {
            SpriteManager.AddDrawableBatch(this);
        }

        /// <summary>
        /// Adds this MapDrawableBatch to the engine (for rendering) on the argument layer.
        /// </summary>
        /// <param name="layer">The layer to add to.</param>
        public void AddToManagers(Layer layer)
        {
            SpriteManager.AddToLayer(this, layer);
        }

        public static MapDrawableBatch FromScnx(string sceneFileName, string contentManagerName, bool verifySameTexturePerLayer)
        {
            // TODO: This line crashes when the path is already absolute!
            string absoluteFileName = FileManager.MakeAbsolute(sceneFileName);

            // TODO: The exception doesn't make sense when the file type is wrong.
            SceneSave saveInstance = SceneSave.FromFile(absoluteFileName);

            int startingIndex = 0;

            string oldRelativeDirectory = FileManager.RelativeDirectory;
            FileManager.RelativeDirectory = FileManager.GetDirectory(absoluteFileName);

            // get the list of sprites from our map file
            List<SpriteSave> spriteSaveList = saveInstance.SpriteList;

            // we use the sprites as defined in the scnx file to create and draw the map.
            MapDrawableBatch mMapBatch = FromSpriteSaves(spriteSaveList, startingIndex, spriteSaveList.Count, contentManagerName, verifySameTexturePerLayer);

            FileManager.RelativeDirectory = oldRelativeDirectory;
            // temp
            //mMapBatch = new MapDrawableBatch(32, 32, 32f, 64, @"content/tiles");
            return mMapBatch;

        }

        /* This creates a MapDrawableBatch (MDB) from the list of sprites provided to us by the FlatRedBall (FRB) Scene XML (scnx) file. */
        public static MapDrawableBatch FromSpriteSaves(List<SpriteSave> spriteSaveList, int startingIndex, int count, string contentManagerName, bool verifySameTexturesPerLayer)
        {

#if DEBUG
            if (verifySameTexturesPerLayer)
            {
                VerifySingleTexture(spriteSaveList, startingIndex, count);
            }
#endif

            // We got it!  We are going to make some assumptions:
            // First we need the texture.  We'll assume all Sprites
            // use the same texture:

            // TODO: I (Bryan) really HATE this assumption. But it will work for now.
            SpriteSave firstSprite = spriteSaveList[startingIndex];

            // This is the file name of the texture, but the file name is relative to the .scnx location
            string textureRelativeToScene = firstSprite.Texture;
            // so we load the texture
            Texture2D texture = FlatRedBallServices.Load<Texture2D>(textureRelativeToScene, contentManagerName);

            if (!MathFunctions.IsPowerOfTwo(texture.Width) || !MathFunctions.IsPowerOfTwo(texture.Height))
            {
                throw new Exception("The dimensions of the texture file " + texture.Name + " are not power of 2!");
            }


            // Assume all the dimensions of the textures are the same. I.e. all tiles use the same texture width and height. 
            // This assumption is safe for Iso and Ortho tile maps.
            int tileFileDimensionsWidth = 0;
            int tileFileDimensionsHeight = 0;
            if (spriteSaveList.Count > startingIndex)
            {
                SpriteSave s = spriteSaveList[startingIndex];

                // deduce the dimensionality of the tile from the texture coordinates
                tileFileDimensionsWidth = (int)System.Math.Round((double)((s.RightTextureCoordinate - s.LeftTextureCoordinate) * texture.Width));
                tileFileDimensionsHeight = (int)System.Math.Round((double)((s.BottomTextureCoordinate - s.TopTextureCoordinate) * texture.Height));

            }


            // alas, we create the MDB 
            MapDrawableBatch mMapBatch = new MapDrawableBatch(count, tileFileDimensionsWidth, tileFileDimensionsHeight, texture);

            int lastIndexExclusive = startingIndex + count;

            for (int i = startingIndex; i < lastIndexExclusive; i++)
            {
                SpriteSave spriteSave = spriteSaveList[i];

                // We don't want objects within the IDB to have a different Z than the IDB itself
                // (if possible) because that makes the IDB behave differently when using sorting vs.
                // the zbuffer.
                const bool setZTo0 = true;
                mMapBatch.Paste(spriteSave, setZTo0);

            }



            return mMapBatch;
        }

        public MapDrawableBatch Clone()
        {
            return base.Clone<MapDrawableBatch>();
        }

        // Bring the texture coordinates in to adjust for rendering issues on dx9/ogl
        public static float CoordinateAdjustment = .00002f;

        internal static MapDrawableBatch FromReducedLayer(TMXGlueLib.DataTypes.ReducedLayerInfo reducedLayerInfo, LayeredTileMap owner, TMXGlueLib.DataTypes.ReducedTileMapInfo rtmi, string contentManagerName)
        {
            int tileDimensionWidth = reducedLayerInfo.TileWidth;
            int tileDimensionHeight = reducedLayerInfo.TileHeight;
            float quadWidth = reducedLayerInfo.TileWidth;
            float quadHeight = reducedLayerInfo.TileHeight;

            string textureName = reducedLayerInfo.Texture;


#if (IOS || ANDROID) && !NET8_0_OR_GREATER

			textureName = textureName.ToLowerInvariant();

#endif

            Texture2D texture = null;
            if (!string.IsNullOrEmpty(textureName))
            {
                texture = FlatRedBallServices.Load<Texture2D>(textureName, contentManagerName);
            }

            MapDrawableBatch toReturn = new MapDrawableBatch(reducedLayerInfo.Quads.Count, tileDimensionWidth, tileDimensionHeight, texture);

            toReturn.Name = reducedLayerInfo.Name;

            Vector3 position = new Vector3();


            TMXGlueLib.DataTypes.ReducedQuadInfo[] quads = null;

            if (rtmi.TileOrientation == TileOrientation.Isometric)
            {
                quads = reducedLayerInfo.Quads.OrderByDescending(item => item.BottomQuadCoordinate).ToArray();
                toReturn.mSortAxis = SortAxis.YTopDown;
            }
            else if (rtmi.NumberCellsWide > rtmi.NumberCellsTall)
            {
                quads = reducedLayerInfo.Quads.OrderBy(item => item.LeftQuadCoordinate).ToArray();
                toReturn.mSortAxis = SortAxis.X;
            }
            else
            {
                quads = reducedLayerInfo.Quads.OrderBy(item => item.BottomQuadCoordinate).ToArray();
                toReturn.mSortAxis = SortAxis.Y;
            }

            var quadLength = quads.Length;
            for (int i = 0; i < quadLength; i++)
            {
                var quad = quads[i];

                Vector2 tileDimensions = new Vector2(quadWidth, quadHeight);
                if (quad.OverridingWidth != null)
                {
                    tileDimensions.X = quad.OverridingWidth.Value;
                }
                if (quad.OverridingHeight != null)
                {
                    tileDimensions.Y = quad.OverridingHeight.Value;
                }
                position.X = quad.LeftQuadCoordinate;
                position.Y = quad.BottomQuadCoordinate;

                // The Z of the quad should be relative to this layer, not absolute Z values.
                // A multi-layer map will offset the individual layer Z values, the quads should have a Z of 0.
                // position.Z = reducedLayerInfo.Z;


                var textureValues = new Vector4();

                // The purpose of CoordinateAdjustment is to bring the texture values "in", to reduce the chance of adjacent
                // tiles drawing on a given tile quad. If we don't do this, we can get slivers of adjacent colors appearing, causing
                // lines or grid patterns.
                // To bring the values "in" we have to consider rotated quads. 
                textureValues.X = CoordinateAdjustment + (float)quad.LeftTexturePixel / (float)texture.Width; // Left
                textureValues.Y = -CoordinateAdjustment + (float)(quad.LeftTexturePixel + tileDimensionWidth) / (float)texture.Width; // Right
                textureValues.Z = CoordinateAdjustment + (float)quad.TopTexturePixel / (float)texture.Height; // Top
                textureValues.W = -CoordinateAdjustment + (float)(quad.TopTexturePixel + tileDimensionHeight) / (float)texture.Height; // Bottom

                // pad before doing any rotations/flipping
                const bool pad = true;
                float amountToAddX = .0000001f;
                float amountToAddY = .0000001f;
                if (texture != null)
                {
                    amountToAddX = .037f / texture.Width;
                    amountToAddY = .037f / texture.Height;
                }
                if (pad)
                {
                    textureValues.X += amountToAddX; // Left
                    textureValues.Y -= amountToAddX; // Right
                    textureValues.Z += amountToAddY; // Top
                    textureValues.W -= amountToAddY; // Bottom
                }

                if ((quad.FlipFlags & TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedHorizontallyFlag) == TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedHorizontallyFlag)
                {
                    var temp = textureValues.Y;
                    textureValues.Y = textureValues.X;
                    textureValues.X = temp;
                }

                if ((quad.FlipFlags & TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedVerticallyFlag) == TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedVerticallyFlag)
                {
                    var temp = textureValues.Z;
                    textureValues.Z = textureValues.W;
                    textureValues.W = temp;
                }

                toReturn.FlipFlagArray[i] = quad.FlipFlags;

                int tileIndex = toReturn.AddTile(position, tileDimensions,
                    //quad.LeftTexturePixel, quad.TopTexturePixel, quad.LeftTexturePixel + tileDimensionWidth, quad.TopTexturePixel + tileDimensionHeight);
                    textureValues);

                if ((quad.FlipFlags & TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedDiagonallyFlag) == TMXGlueLib.DataTypes.ReducedQuadInfo.FlippedDiagonallyFlag)
                {
                    toReturn.ApplyDiagonalFlip(tileIndex);
                }

                // This was moved to outside of this conversion, to support shaps
                //if (quad.QuadSpecificProperties != null)
                //{
                //    var listToAdd = quad.QuadSpecificProperties.ToList();
                //    listToAdd.Add(new NamedValue { Name = "Name", Value = quad.Name });
                //    owner.Properties.Add(quad.Name, listToAdd);
                //}
                if (quad.RotationDegrees != 0)
                {
                    // Tiled rotates clockwise :(
                    var rotationRadians = -MathHelper.ToRadians(quad.RotationDegrees);

                    Vector3 bottomLeftPos = toReturn.Vertices[tileIndex * 4].Position;

                    Vector3 vertPos = toReturn.Vertices[tileIndex * 4 + 1].Position;
                    MathFunctions.RotatePointAroundPoint(bottomLeftPos, ref vertPos, rotationRadians);
                    toReturn.Vertices[tileIndex * 4 + 1].Position = vertPos;

                    vertPos = toReturn.Vertices[tileIndex * 4 + 2].Position;
                    MathFunctions.RotatePointAroundPoint(bottomLeftPos, ref vertPos, rotationRadians);
                    toReturn.Vertices[tileIndex * 4 + 2].Position = vertPos;

                    vertPos = toReturn.Vertices[tileIndex * 4 + 3].Position;
                    MathFunctions.RotatePointAroundPoint(bottomLeftPos, ref vertPos, rotationRadians);
                    toReturn.Vertices[tileIndex * 4 + 3].Position = vertPos;

                }

                toReturn.RegisterName(quad.Name, tileIndex);
            }

            toReturn.ParallaxMultiplierX = reducedLayerInfo.ParallaxMultiplierX;
            toReturn.ParallaxMultiplierY = reducedLayerInfo.ParallaxMultiplierY;

            return toReturn;
        }

        public void Paste(Sprite sprite)
        {
            Paste(sprite, false);
        }

        public int Paste(Sprite sprite, bool setZTo0)
        {
            // here we have the Sprite's X and Y in absolute coords as well as its texture coords
            // NOTE: I appended the Z coordinate for the sake of iso maps. This SHOULDN'T have an effect on the ortho maps since I believe the 
            // TMX->SCNX tool sets all z to zero.

            // The AddTile method expects the bottom-left corner
            float x = sprite.X - sprite.ScaleX;
            float y = sprite.Y - sprite.ScaleY;
            float z = sprite.Z;

            if (setZTo0)
            {
                z = 0;
            }

            float width = 2f * sprite.ScaleX; // w
            float height = 2f * sprite.ScaleY; // z

            float topTextureCoordinate = sprite.TopTextureCoordinate;
            float bottomTextureCoordinate = sprite.BottomTextureCoordinate;
            float leftTextureCoordinate = sprite.LeftTextureCoordinate;
            float rightTextureCoordinate = sprite.RightTextureCoordinate;

            int tileIndex = mCurrentNumberOfTiles;

            RegisterName(sprite.Name, tileIndex);

            // add the textured tile to our map so that we may draw it.
            return AddTile(new Vector3(x, y, z),
                new Vector2(width, height),
                new Vector4(leftTextureCoordinate, rightTextureCoordinate, topTextureCoordinate, bottomTextureCoordinate));

        }

        public void Paste(SpriteSave spriteSave)
        {
            Paste(spriteSave, false);
        }

        public int Paste(SpriteSave spriteSave, bool setZTo0)
        {
            // here we have the Sprite's X and Y in absolute coords as well as its texture coords
            // NOTE: I appended the Z coordinate for the sake of iso maps. This SHOULDN'T have an effect on the ortho maps since I believe the 
            // TMX->SCNX tool sets all z to zero.

            // The AddTile method expects the bottom-left corner
            float x = spriteSave.X - spriteSave.ScaleX;
            float y = spriteSave.Y - spriteSave.ScaleY;
            float z = spriteSave.Z;
            if (setZTo0)
            {
                z = 0;
            }

            float width = 2f * spriteSave.ScaleX; // w
            float height = 2f * spriteSave.ScaleY; // z

            float topTextureCoordinate = spriteSave.TopTextureCoordinate;
            float bottomTextureCoordinate = spriteSave.BottomTextureCoordinate;
            float leftTextureCoordinate = spriteSave.LeftTextureCoordinate;
            float rightTextureCoordinate = spriteSave.RightTextureCoordinate;

            int tileIndex = mCurrentNumberOfTiles;

            RegisterName(spriteSave.Name, tileIndex);

            // add the textured tile to our map so that we may draw it.
            return AddTile(new Vector3(x, y, z), new Vector2(width, height), new Vector4(leftTextureCoordinate, rightTextureCoordinate, topTextureCoordinate, bottomTextureCoordinate));
        }

        private static void VerifySingleTexture(List<SpriteSave> spriteSaveList, int startingIndex, int count)
        {
            // Every Sprite should either have the same texture
            if (spriteSaveList.Count != 0)
            {
                string texture = spriteSaveList[startingIndex].Texture;

                for (int i = startingIndex + 1; i < startingIndex + count; i++)
                {
                    SpriteSave ss = spriteSaveList[i];

                    if (ss.Texture != texture)
                    {
                        float leftOfSprite = ss.X - ss.ScaleX;
                        float indexX = leftOfSprite / (ss.ScaleX * 2);

                        float topOfSprite = ss.Y + ss.ScaleY;
                        float indexY = (0 - topOfSprite) / (ss.ScaleY * 2);

                        throw new Exception("All Sprites do not have the same texture");
                    }
                }

            }
        }

        public void RegisterName(string name, int tileIndex)
        {
            int throwaway;
            if (!string.IsNullOrEmpty(name) && !int.TryParse(name, out throwaway))
            {
                // TEMPORARY:
                // The tmx converter
                // names all Sprites with
                // a number if their name is
                // not explicitly set.  Therefore
                // we have to ignore those and look
                // for explicit names (names not numbers).
                // Will talk to Domenic about this to fix it.
                if (!mNamedTileOrderedIndexes.ContainsKey(name))
                {
                    mNamedTileOrderedIndexes.Add(name, new List<int>());
                }

                mNamedTileOrderedIndexes[name].Add(tileIndex);
            }
        }

        Vector2[] coords = new Vector2[4];

        /// <summary>
        /// Paints a texture on a tile.  This method takes the index of the Sprite in the order it was added
        /// to the MapDrawableBatch, so it supports any configuration including non-rectangular maps and maps with
        /// gaps.
        /// </summary>
        /// <example>
        /// newTextureId is the ID of the tile within the referenced tileset. Each ID represents one square in the texture. For example, consider
        /// a tileset which is 64x64 pixels, and each tile in the tileset is 16 pixels wide and tall. In this case, a newTextureId of 0
        /// would paint a tile with the the top-left 16x16 pixel in the referenced tileset. Since the tileset in this example is 4 tiles wide
        /// (64 pixels wide, each tile is 16 pixels wide), the following indexes would reference the following sections of the tileset:
        /// 0: top-left tile
        /// 3: top-right tile
        /// 4: left-most tile on the 2nd row
        /// 7: right-most tile on the 2nd row
        /// 12: bottom-left tile 
        /// 15: bottom-right tile
        /// In this case 15 is the last tile index, so values of 16 and greater are invalid.
        /// </example>
        /// <param name="orderedTileIndex">The index of the tile to paint - this matches the index of the tile as it was added.</param>
        /// <param name="newTextureId">The ID of the tile in the texture, where 0 is the top-left tile. Increasing this value moves to the right until the tile reaches
        /// the end of the first row. After that, the next index is the first column on the second row. See remarks for an example.</param>
        public void PaintTile(int orderedTileIndex, int newTextureId)
        {
            int currentVertex = orderedTileIndex * 4; // 4 vertices per tile

            // Reusing the coords array saves us on allocation
            mTileset.GetTextureCoordinateVectorsOfTextureIndex(newTextureId, coords);

            // Coords are
            // 3   2
            //
            // 0   1

            mVertices[currentVertex + 0].TextureCoordinate = coords[0];
            mVertices[currentVertex + 1].TextureCoordinate = coords[1];
            mVertices[currentVertex + 2].TextureCoordinate = coords[2];
            mVertices[currentVertex + 3].TextureCoordinate = coords[3];

        }

        /// <summary>
        /// Sets the left and top texture coordiantes of the tile represented by orderedTileIndex. The right and bottom texture coordaintes
        /// are set automatically according to the tileset dimensions.
        /// </summary>
        /// <param name="orderedTileIndex">The ordered tile index.</param>
        /// <param name="textureXCoordinate">The left texture coordiante (in UV coordinates)</param>
        /// <param name="textureYCoordinate">The top texture coordainte (in UV coordinates)</param>
        public void PaintTileTextureCoordinates(int orderedTileIndex, float textureXCoordinate, float textureYCoordinate)
        {
            int currentVertex = orderedTileIndex * 4; // 4 vertices per tile

            mTileset.GetCoordinatesForTile(coords, textureXCoordinate, textureYCoordinate);

            mVertices[currentVertex + 0].TextureCoordinate = coords[0];
            mVertices[currentVertex + 1].TextureCoordinate = coords[1];
            mVertices[currentVertex + 2].TextureCoordinate = coords[2];
            mVertices[currentVertex + 3].TextureCoordinate = coords[3];
        }

        public void PaintTileTextureCoordinates(int orderedTileIndex, float leftCoordinate, float topCoordinate, float rightCoordinate, float bottomCoordinate)
        {
            int currentVertex = orderedTileIndex * 4; // 4 vertices per tile

            // Coords are
            // 3   2
            //
            // 0   1

            mVertices[currentVertex + 0].TextureCoordinate.X = leftCoordinate;
            mVertices[currentVertex + 0].TextureCoordinate.Y = bottomCoordinate;

            mVertices[currentVertex + 1].TextureCoordinate.X = rightCoordinate;
            mVertices[currentVertex + 1].TextureCoordinate.Y = bottomCoordinate;

            mVertices[currentVertex + 2].TextureCoordinate.X = rightCoordinate;
            mVertices[currentVertex + 2].TextureCoordinate.Y = topCoordinate;

            mVertices[currentVertex + 3].TextureCoordinate.X = leftCoordinate;
            mVertices[currentVertex + 3].TextureCoordinate.Y = topCoordinate;
        }




        // Swaps the top-right for the bottom-left verts
        public void ApplyDiagonalFlip(int orderedTileIndex)
        {
            int currentVertex = orderedTileIndex * 4; // 4 vertices per tile

            // Coords are
            // 3   2
            //
            // 0   1

            var old0 = mVertices[currentVertex + 0].TextureCoordinate;

            mVertices[currentVertex + 0].TextureCoordinate = mVertices[currentVertex + 2].TextureCoordinate;
            mVertices[currentVertex + 2].TextureCoordinate = old0;
        }

        public void RotateTextureCoordinatesCounterclockwise(int orderedTileIndex)
        {
            int currentVertex = orderedTileIndex * 4; // 4 vertices per tile

            // Coords are
            // 3   2
            //
            // 0   1

            var old3 = mVertices[currentVertex + 3].TextureCoordinate;

            mVertices[currentVertex + 3].TextureCoordinate = mVertices[currentVertex + 2].TextureCoordinate;
            mVertices[currentVertex + 2].TextureCoordinate = mVertices[currentVertex + 1].TextureCoordinate;
            mVertices[currentVertex + 1].TextureCoordinate = mVertices[currentVertex + 0].TextureCoordinate;
            mVertices[currentVertex + 0].TextureCoordinate = old3;

        }

        public void GetTextureCoordiantesForOrderedTile(int orderedTileIndex, out float textureX, out float textureY)
        {
            // The order is:
            // 3   2
            //
            // 0   1

            // So we want to add 3 to the index to get the top-left vert, then use
            // the texture coordinates there to get the 
            Vector2 vector = mVertices[(orderedTileIndex * 4) + 3].TextureCoordinate;

            textureX = vector.X;
            textureY = vector.Y;
        }

        public float GetRotationZForOrderedTile(int orderedTileIndex)
        {
            // The order is:
            // 3   2
            //
            // 0   1

            // So that means 
            // 3           2
            // ^
            // |
            // Y
            // |
            // 0 ---X--->  1

            // We can't use positions because for rotated tiles the positions stay the same, but the
            // texture coordiantes do not. So we should use the texture coordiantes.
            // A tile's texture coordiantes may look like this:
            // (0, 0)       (1, 0)
            //
            //
            //
            //
            // (0, 1)       (1, 1)
            // 

            // The X Axis is set by finding the pair of texture coordinates where
            // the Y value is the same, and the X value is increasing
            // There may be a more elegant way to do this (mathematically)
            // but I'm not sure what that is so we'll just brute force it:
            var startIndex = orderedTileIndex * 4;

            var bottomLeft = mVertices[startIndex];
            var bottomRight = mVertices[startIndex + 1];
            var topRight = mVertices[startIndex + 2];
            var topLeft = mVertices[startIndex + 3];

            Vector3 xAxis = Vector3.UnitX;

            if (bottomLeft.TextureCoordinate.Y == bottomRight.TextureCoordinate.Y)
            {
                if (bottomRight.TextureCoordinate.X > bottomLeft.TextureCoordinate.X)
                {
                    xAxis = bottomRight.Position - bottomLeft.Position;
                }
                else
                {
                    xAxis = bottomLeft.Position - bottomRight.Position;
                }
            }
            else
            {
                // use top right and bottom right
                if (topRight.TextureCoordinate.Y == bottomRight.TextureCoordinate.Y)
                {
                    if (topRight.TextureCoordinate.X > bottomRight.TextureCoordinate.X)
                    {
                        xAxis = topRight.Position - bottomRight.Position;
                    }
                    else
                    {
                        xAxis = bottomRight.Position - topRight.Position;
                    }
                }
            }

            var rotationZ = (float)System.Math.Atan2(xAxis.Y, xAxis.X);
            if (rotationZ < 0)
            {
                rotationZ += MathHelper.TwoPi;
            }

            return rotationZ;
        }

        public void GetBottomLeftWorldCoordinateForOrderedTile(int orderedTileIndex, out float x, out float y)
        {
            // The order is:
            // 3   2
            //
            // 0   1

            // So we just need to mutiply by 4 and not add anything
            Vector3 vector = mVertices[(orderedTileIndex * 4)].Position;

            x = vector.X;
            y = vector.Y;
        }

        /// <summary>
        /// Returns the quad index at the argument worldX and worldY. Returns null if no quad is found at this index.
        /// If the map has a SortAxis of X or Y, then this funcion uses the sorting to find the quad more quickly.
        /// </summary>
        /// <param name="worldX">The absolute world X position.</param>
        /// <param name="worldY">The absolute world Y position.</param>
        /// <returns>The index found, or null if one isn't found.</returns>
        public int? GetQuadIndex(float worldX, float worldY)
        {
            if (mVertices.Length == 0)
            {
                return null;
            }

            var firstVertIndex = 0;

            var lastVertIndexExclusive = mVertices.Length;

            float tileWidth = mVertices[1].Position.X - mVertices[0].Position.X;

            if (mSortAxis == SortAxis.X)
            {
                firstVertIndex = GetFirstAfterX(mVertices, worldX - tileWidth);
                lastVertIndexExclusive = GetFirstAfterX(mVertices, worldX + tileWidth);
            }
            else if (mSortAxis == SortAxis.Y)
            {
                firstVertIndex = GetFirstAfterY(mVertices, worldY - tileWidth);
                lastVertIndexExclusive = GetFirstAfterY(mVertices, worldY + tileWidth);
            }

            for (int i = firstVertIndex; i < lastVertIndexExclusive; i += 4)
            {
                // Coords are
                // 3   2
                //
                // 0   1

                if (mVertices[i + 0].Position.X <= worldX && mVertices[i + 0].Position.Y <= worldY &&
                    mVertices[i + 1].Position.X >= worldX && mVertices[i + 1].Position.Y <= worldY &&
                    mVertices[i + 2].Position.X >= worldX && mVertices[i + 2].Position.Y >= worldY &&
                    mVertices[i + 3].Position.X <= worldX && mVertices[i + 3].Position.Y >= worldY)
                {
                    return i / 4;
                }
            }


            return null;
        }

        /// <summary>
        /// Most pixel distortion problems can be solved by snapping the camera position 
        /// to the pixel using MathFunctions.RoundFloat or using the Camera Controlling Entity.
        /// For stubborn distortions only happening on Tiled maps you can use this. It won't 
        /// do anything unless set to something different to zero.
        /// </summary>
        public static float TileVertexOffset { get; set; }

        /// <summary>
        /// Adds a tile to the tile map
        /// </summary>
        /// <param name="bottomLeftPosition"></param>
        /// <param name="dimensions"></param>
        /// <param name="texture">
        ///     4 points defining the boundaries in the texture for the tile.
        ///     (X = left, Y = right, Z = top, W = bottom)
        /// </param>
        /// <returns>The index of the tile in the tile map, which can be used to modify the painted tile at a later time.</returns>
        public int AddTile(Vector3 bottomLeftPosition, Vector2 dimensions, Vector4 texture)
        {
            int toReturn = mCurrentNumberOfTiles;
            int currentVertex = mCurrentNumberOfTiles * 4;

            int currentIndex = mCurrentNumberOfTiles * 6; // 6 indices per tile (there are mVertices.Length/4 tiles)

            float xOffset = bottomLeftPosition.X + TileVertexOffset;
            float yOffset = bottomLeftPosition.Y + TileVertexOffset;
            float zOffset = bottomLeftPosition.Z;

            float width = dimensions.X - (TileVertexOffset * 2f);
            float height = dimensions.Y - (TileVertexOffset * 2f);

            // create vertices
            mVertices[currentVertex + 0] = new VertexType(new Vector3(xOffset + 0f, yOffset + 0f, zOffset), new Vector2(texture.X, texture.W));
            mVertices[currentVertex + 1] = new VertexType(new Vector3(xOffset + width, yOffset + 0f, zOffset), new Vector2(texture.Y, texture.W));
            mVertices[currentVertex + 2] = new VertexType(new Vector3(xOffset + width, yOffset + height, zOffset), new Vector2(texture.Y, texture.Z));
            mVertices[currentVertex + 3] = new VertexType(new Vector3(xOffset + 0f, yOffset + height, zOffset), new Vector2(texture.X, texture.Z));

            // create indices
            if(indices32Bit != null)
            {
                indices32Bit[currentIndex + 0] = currentVertex + 0;
                indices32Bit[currentIndex + 1] = currentVertex + 1;
                indices32Bit[currentIndex + 2] = currentVertex + 2;
                indices32Bit[currentIndex + 3] = currentVertex + 0;
                indices32Bit[currentIndex + 4] = currentVertex + 2;
                indices32Bit[currentIndex + 5] = currentVertex + 3;
            }
            else
            {
                indices16Bit[currentIndex + 0] = (short)(currentVertex + 0);
                indices16Bit[currentIndex + 1] = (short)(currentVertex + 1);
                indices16Bit[currentIndex + 2] = (short)(currentVertex + 2);
                indices16Bit[currentIndex + 3] = (short)(currentVertex + 0);
                indices16Bit[currentIndex + 4] = (short)(currentVertex + 2);
                indices16Bit[currentIndex + 5] = (short)(currentVertex + 3);
            }

            mCurrentNumberOfTiles++;

            return toReturn;
        }

        /// <summary>
        /// Add a tile to the map
        /// </summary>
        /// <param name="bottomLeftPosition"></param>
        /// <param name="tileDimensions"></param>
        /// <param name="textureTopLeftX">Top left pixel X coordinate in the core texture</param>
        /// <param name="textureTopLeftY">Top left pixel Y coordinate in the core texture</param>
        /// <param name="textureBottomRightX">Bottom right pixel X coordinate in the core texture</param>
        /// <param name="textureBottomRightY">Bottom right pixel Y coordinate in the core texture</param>
        public int AddTile(Vector3 bottomLeftPosition, Vector2 tileDimensions, int textureTopLeftX, int textureTopLeftY, int textureBottomRightX, int textureBottomRightY)
        {
            // Form vector4 for AddTile overload
            var textureValues = new Vector4();
            textureValues.X = (float)textureTopLeftX / (float)mTexture.Width; // Left
            textureValues.Y = (float)textureBottomRightX / (float)mTexture.Width; // Right
            textureValues.Z = (float)textureTopLeftY / (float)mTexture.Height; // Top
            textureValues.W = (float)textureBottomRightY / (float)mTexture.Height; // Bottom

            return AddTile(bottomLeftPosition, tileDimensions, textureValues);
        }

        /// <summary>
        /// Renders the MapDrawableBatch
        /// </summary>
        /// <param name="camera">The currently drawing camera</param>
        public void Draw(Camera camera)
        {
            ////////////////////Early Out///////////////////

            if (!AbsoluteVisible)
            {
                return;
            }
            if (mVertices.Length == 0)
            {
                return;
            }

            //////////////////End Early Out/////////////////


            AdjustOffsetAndParallax(camera);

            ForceUpdateDependencies();

            int firstVertIndex;
            int lastVertIndex;
            int indexStart;
            int numberOfTriangles;
            GetRenderingIndexValues(camera, out firstVertIndex, out lastVertIndex, out indexStart, out numberOfTriangles);

            if (numberOfTriangles != 0)
            {
                TextureFilter? oldTextureFilter = null;

                if (this.TextureFilter != null && this.TextureFilter != FlatRedBallServices.GraphicsOptions.TextureFilter)
                {
                    oldTextureFilter = FlatRedBallServices.GraphicsOptions.TextureFilter;
                    FlatRedBallServices.GraphicsOptions.TextureFilter = this.TextureFilter.Value;
                }
                TextureAddressMode oldTextureAddressMode;
                FlatRedBall.Graphics.BlendOperation oldBlendOp;
                Effect effectTouse = PrepareRenderingStates(camera, out oldTextureAddressMode, out oldBlendOp);

                foreach (EffectPass pass in effectTouse.CurrentTechnique.Passes)
                {
                    // Start each pass

                    pass.Apply();

                    int numberVertsToDraw = lastVertIndex - firstVertIndex;

                    // Right now this uses the (slower) DrawUserIndexedPrimitives
                    // It could use DrawIndexedPrimitives instead for much faster performance,
                    // but to do that we'd have to keep VB's around and make sure to re-create them
                    // whenever the graphics device is lost. Also, this would not work if tiles are animated
                    // since those change their texture coordiantes. We can be more intelligent about this, though
                    // but for now this is not even close to the slowest part of the engine so we'll leave it as is.
                    if(indices32Bit != null)
                    {
                        FlatRedBallServices.GraphicsDevice.DrawUserIndexedPrimitives<VertexType>(
                            PrimitiveType.TriangleList,
                            mVertices,
                            firstVertIndex,
                            numberVertsToDraw,
                            indices32Bit,
                            indexStart, numberOfTriangles);
                    }
                    else
                    {
                        FlatRedBallServices.GraphicsDevice.DrawUserIndexedPrimitives<VertexType>(
                            PrimitiveType.TriangleList,
                            mVertices,
                            firstVertIndex,
                            numberVertsToDraw,
                            indices16Bit,
                            indexStart, numberOfTriangles);
                    }

                }

                Renderer.TextureAddressMode = oldTextureAddressMode;
                FlatRedBall.Graphics.Renderer.BlendOperation = oldBlendOp;

                if (ZBuffered)
                {
                    FlatRedBallServices.GraphicsDevice.DepthStencilState = DepthStencilState.DepthRead;
                }
                if (oldTextureFilter != null)
                {
                    FlatRedBallServices.GraphicsOptions.TextureFilter = oldTextureFilter.Value;
                }
            }
        }

        private Effect PrepareRenderingStates(Camera camera, out TextureAddressMode oldTextureAddressMode, out FlatRedBall.Graphics.BlendOperation oldBlendOperation)
        {
            // Set graphics states
            FlatRedBallServices.GraphicsDevice.RasterizerState = RasterizerState.CullNone;

            oldBlendOperation = FlatRedBall.Graphics.Renderer.BlendOperation;

#if TILEMAPS_ALPHA_AND_COLOR
            FlatRedBall.Graphics.Renderer.BlendOperation = BlendOperation.Regular;
            FlatRedBall.Graphics.Renderer.ColorOperation = ColorOperation.Modulate;
#else
            FlatRedBall.Graphics.Renderer.BlendOperation = BlendOperation.Regular;
#endif
            Effect effectToUse = null;

            if (ZBuffered)
            {
                FlatRedBallServices.GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                camera.SetDeviceViewAndProjection(mAlphaTestEffect, false);

                mAlphaTestEffect.World = Matrix.CreateScale(RenderingScale) * base.TransformationMatrix;
                mAlphaTestEffect.Texture = mTexture;

                effectToUse = mAlphaTestEffect;
            }
            else
            {
#if RendererHasExternalEffectManager && MONOGAME_381
                if (UseCustomEffect)
                {
                    var effectManager = Renderer.ExternalEffectManager;

                    var world = Matrix.CreateScale(RenderingScale) * base.TransformationMatrix;
                    var view = camera.GetLookAtMatrix(false);
                    var projection = camera.GetProjectionMatrix();
                    var worldView = Matrix.Identity;
                    var worldViewProj = Matrix.Identity;

                    Matrix.Multiply(ref world, ref view, out worldView);
                    Matrix.Multiply(ref worldView, ref projection, out worldViewProj);

                    effectManager.ParameterViewProj.SetValue(worldViewProj);
                    effectManager.ParameterCurrentTexture.SetValue(mTexture);

                    var color = CustomEffectManager.ProcessColorForColorOperation(mColorOperation, new Vector4(mRed, mGreen, mBlue, mAlpha));
                    effectManager.ParameterColorModifier.SetValue(color);

                    effectToUse = effectManager.Effect;

                    var effectTechnique = effectManager.GetColorModifierTechniqueFromColorOperation(mColorOperation);

                    if (effectToUse.CurrentTechnique != effectTechnique)
                        effectToUse.CurrentTechnique = effectTechnique;
                }
                else
#endif
                {
                    camera.SetDeviceViewAndProjection(mBasicEffect, false);

                    mBasicEffect.World = Matrix.CreateScale(RenderingScale) * base.TransformationMatrix;
                    mBasicEffect.Texture = mTexture;

                    mBasicEffect.DiffuseColor = new Vector3(Red, Green, Blue);
                    mBasicEffect.Alpha = Alpha;

#if TILEMAPS_ALPHA_AND_COLOR
                mBasicEffect.VertexColorEnabled = true;
#endif
                    effectToUse = mBasicEffect;
                }
            }



            // We won't need to use any other kind of texture
            // address mode besides clamp, and clamp is required
            // on the "Reach" profile when the texture is not power
            // of two.  Let's set it to clamp here so that we don't crash
            // on non-power-of-two textures.
            oldTextureAddressMode = Renderer.TextureAddressMode;
            Renderer.TextureAddressMode = TextureAddressMode.Clamp;

            return effectToUse;
        }

        private void GetRenderingIndexValues(Camera camera, out int firstVertIndex, out int lastVertIndex, out int indexStart, out int numberOfTriangles)
        {

            firstVertIndex = 0;

            lastVertIndex = mVertices.Length;

            // We're waiting on Kni to support non-0 vertexOffset values on DrawUserPrimitives and DrawUserIndexedPrimitives
            // When that happens we can remove this and improve performance.
#if !WEB

            float tileWidth = mVertices[1].Position.X - mVertices[0].Position.X;

            if (mSortAxis == SortAxis.X)
            {
                float minX = camera.AbsoluteLeftXEdgeAt(this.Z);
                float maxX = camera.AbsoluteRightXEdgeAt(this.Z);

                minX -= this.X;
                maxX -= this.X;

                firstVertIndex = GetFirstAfterX(mVertices, minX - tileWidth);
                lastVertIndex = GetFirstAfterX(mVertices, maxX) + 4;
            }
            else if (mSortAxis == SortAxis.Y)
            {
                float minY = camera.AbsoluteBottomYEdgeAt(this.Z);
                float maxY = camera.AbsoluteTopYEdgeAt(this.Z);

                minY -= this.Y;
                maxY -= this.Y;

                firstVertIndex = GetFirstAfterY(mVertices, minY - tileWidth);
                lastVertIndex = GetFirstAfterY(mVertices, maxY) + 4;
            }
#endif
            lastVertIndex = System.Math.Min(lastVertIndex, mVertices.Length);

            indexStart = 0;
            int indexEndExclusive = ((lastVertIndex - firstVertIndex) * 3) / 2;

            numberOfTriangles = (indexEndExclusive - indexStart) / 3;
        }

        public static int GetFirstAfterX(VertexType[] list, float xGreaterThan)
        {
            int min = 0;
            int originalMax = list.Length / 4;
            int max = list.Length / 4;

            int mid = (max + min) / 2;

            while (min < max)
            {
                mid = (max + min) / 2;
                float midItem = list[mid * 4].Position.X;

                if (midItem > xGreaterThan)
                {
                    // Is this the last one?
                    // Not sure why this is here, because if we have just 2 items,
                    // this will always return a value of 1 instead 
                    //if (mid * 4 + 4 >= list.Length)
                    //{
                    //    return mid * 4;
                    //}

                    // did we find it?
                    if (mid > 0 && list[(mid - 1) * 4].Position.X <= xGreaterThan)
                    {
                        return mid * 4;
                    }
                    else
                    {
                        max = mid - 1;
                    }
                }
                else if (midItem <= xGreaterThan)
                {
                    if (mid == 0)
                    {
                        return mid * 4;
                    }
                    else if (mid < originalMax - 1 && list[(mid + 1) * 4].Position.X > xGreaterThan)
                    {
                        return (mid + 1) * 4;
                    }
                    else
                    {
                        min = mid + 1;
                    }
                }
            }
            if (min == 0)
            {
                return 0;
            }
            else
            {
                return list.Length;
            }
        }

        public static int GetFirstAfterY(VertexType[] list, float yGreaterThan)
        {
            int min = 0;
            int originalMax = list.Length / 4;
            int max = list.Length / 4;

            int mid = (max + min) / 2;

            while (min < max)
            {
                mid = (max + min) / 2;
                float midItem = list[mid * 4].Position.Y;

                if (midItem > yGreaterThan)
                {
                    // Is this the last one?
                    // See comment in GetFirstAfterX
                    //if (mid * 4 + 4 >= list.Length)
                    //{
                    //    return mid * 4;
                    //}

                    // did we find it?
                    if (mid > 0 && list[(mid - 1) * 4].Position.Y <= yGreaterThan)
                    {
                        return mid * 4;
                    }
                    else
                    {
                        max = mid - 1;
                    }
                }
                else if (midItem <= yGreaterThan)
                {
                    if (mid == 0)
                    {
                        return mid * 4;
                    }
                    else if (mid < originalMax - 1 && list[(mid + 1) * 4].Position.Y > yGreaterThan)
                    {
                        return (mid + 1) * 4;
                    }
                    else
                    {
                        min = mid + 1;
                    }
                }
            }
            if (min == 0)
            {
                return 0;
            }
            else
            {
                return list.Length;
            }
        }

        string GetTileNameAt(float worldX, float worldY)
        {
            var quadIndex = GetQuadIndex(worldX, worldY);

            if (quadIndex != null)
            {
                // look in all the dictionaries to find which names exist at this index
                var foundKeyValuePair = NamedTileOrderedIndexes.FirstOrDefault(item => item.Value.Contains(quadIndex.Value));

                return foundKeyValuePair.Key;
            }
            return null;
        }

        TMXGlueLib.mapTilesetTile GetTilesetTileAt(float worldX, float worldY, LayeredTileMap map)
        {
            TMXGlueLib.mapTilesetTile tilesetTile = null;

            var quadIndex = GetQuadIndex(worldX, worldY);

            if (quadIndex != null)
            {
                // look in all the dictionaries to find which names exist at this index
                var foundKeyValuePair = NamedTileOrderedIndexes.FirstOrDefault(item => item.Value.Contains(quadIndex.Value));

                if (foundKeyValuePair.Key != null)
                {
                    var name = foundKeyValuePair.Key;

                    foreach (var tileset in map.Tilesets)
                    {
                        tilesetTile = GetTilesetTile(tileset, name);

                        if (tilesetTile != null)
                        {
                            break;
                        }
                    }
                }
            }

            return tilesetTile;
        }

        private TMXGlueLib.mapTilesetTile GetTilesetTile(TMXGlueLib.Tileset tileset, string name)
        {
            foreach (var kvp in tileset.TileDictionary)
            {
                var candidate = kvp.Value;
                var nameProperty = candidate.properties.FirstOrDefault(item => item.StrippedNameLower == "name");

                if (nameProperty?.value == name)
                {
                    return candidate;
                }
            }

            return null;
        }

        public void Update()
        {
            var camera = Camera.Main;
            AdjustOffsetAndParallax(camera);

            this.TimedActivity(TimeManager.SecondDifference, TimeManager.SecondDifferenceSquaredDividedByTwo, TimeManager.LastSecondDifference);

            // The MapDrawableBatch may be attached to a LayeredTileMap (the container of all layers)
            // If so, the player may move the LayeredTileMap and expect all contained layers to move along
            // with it.  To allow this, we need to have dependencies updated.  We'll do this by simply updating
            // dependencies here, although I don't know at this point if there's a better way - like if we should
            // be adding this to the SpriteManager's PositionedObjectList.  This is an improvement so we'll do it for
            // now and revisit this in case there's a problem in the future.
            this.UpdateDependencies(TimeManager.CurrentTime);
        }

        private void AdjustOffsetAndParallax(Camera camera)
        {
            float leftView = NativeCameraWidth != null ? camera.X - NativeCameraWidth.Value / 2f : camera.AbsoluteLeftXEdgeAt(0);
            float topView = NativeCameraHeight != null ? camera.Y + NativeCameraHeight.Value / 2.0f : camera.AbsoluteTopYEdgeAt(0);

            float cameraOffsetX = leftView - CameraOriginX;
            float cameraOffsetY = topView - CameraOriginY;

            if (camera.Orthogonal)
            {
                var zoom = camera.DestinationRectangle.Height / camera.OrthogonalHeight;

                var pixelRoundingValue = 1 / zoom;
                pixelRoundingValue = System.Math.Min(1, pixelRoundingValue);

                this.RelativeX = MathFunctions.RoundFloat(cameraOffsetX * _parallaxMultiplierX, pixelRoundingValue);
                this.RelativeY = MathFunctions.RoundFloat(cameraOffsetY * _parallaxMultiplierY, pixelRoundingValue);


            }

            else
            {
                this.RelativeX = cameraOffsetX * _parallaxMultiplierX;
                this.RelativeY = cameraOffsetY * _parallaxMultiplierY;
            }
        }

        public static float? NativeCameraWidth;
        public static float? NativeCameraHeight;

        // TODO: I would like to somehow make this a property on the LayeredTileMap, but right now it is easier to put them here
        public float CameraOriginY { get; set; }
        public float CameraOriginX { get; set; }

        IVisible IVisible.Parent
        {
            get
            {
                return this.Parent as IVisible;
            }
        }

        public bool AbsoluteVisible
        {
            get
            {
                if (this.Visible)
                {
                    var parentAsIVisible = this.Parent as IVisible;

                    if (parentAsIVisible == null || IgnoresParentVisibility)
                    {
                        return true;
                    }
                    else
                    {
                        // this is true, so return if the parent is visible:
                        return parentAsIVisible.AbsoluteVisible;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        public bool IgnoresParentVisibility
        {
            get;
            set;
        }

        /// <summary>
        /// Don't call this, instead call SpriteManager.RemoveDrawableBatch
        /// </summary>
        public void Destroy()
        {
            this.RemoveSelfFromListsBelongingTo();
        }


        public void MergeOntoThis(IEnumerable<MapDrawableBatch> mapDrawableBatches)
        {
            int quadsOnThis = QuadCount;

            // If this is empty, then this will inherit the first MDB's texture

            if (quadsOnThis == 0 && this.Texture == null)
            {
                var firstWithNonNullTexture = mapDrawableBatches.FirstOrDefault(item => item.Texture != null);

                this.Texture = firstWithNonNullTexture?.Texture;
            }


#if DEBUG
            var thisTexture = this.Texture;
            foreach (var mdb in mapDrawableBatches)
            {
                if(mdb.Texture != thisTexture && mdb.QuadCount > 0)
                {
                    string thisTextureName = thisTexture?.Name ?? "<null>";
                    string otherTexture = mdb.Texture?.Name ?? "<null>";

                    throw new InvalidOperationException($"The MapDrawableBatch {mdb.Name} has the texture {otherTexture} which is different than this layer's texture {thisTextureName}");
                }
                if(mdb.Z < this.Z)
                {
                    throw new Exception(
                        $"The layer {mdb.Name} has a lower Z {mdb.Z} than this layer's Z ({this.Name} {this.Z}). Merging has to be called on the bottom-most layer");

                }
            }
#endif

            int quadsToAdd = 0;
            foreach (var mdb in mapDrawableBatches)
            {
                quadsToAdd += mdb.QuadCount;
            }



            int totalNumberOfVerts = 4 * (this.QuadCount + quadsToAdd);
            int totalNumberOfIndexes = 6 * (this.QuadCount + quadsToAdd);

            var oldVerts = mVertices;

            if (this.SortAxis == SortAxis.X)
            {
                MergeSortedX(mapDrawableBatches, totalNumberOfVerts, totalNumberOfIndexes);
            }
            else if (this.SortAxis == SortAxis.Y)
            {
                MergeSortedY(mapDrawableBatches, totalNumberOfVerts, totalNumberOfIndexes);
            }
            else
            {
                var old32BitIndexes = indices32Bit;
                var old16BitIndexes = indices16Bit;
                MergeUnsorted(mapDrawableBatches, quadsOnThis, totalNumberOfVerts, totalNumberOfIndexes, oldVerts, old32BitIndexes, old16BitIndexes);
            }
        }

        private void MergeSortedX(IEnumerable<MapDrawableBatch> mapDrawableBatches, int totalNumberOfVerts, int totalNumberOfIndexes)
        {
            if (totalNumberOfVerts == this.mVertices.Length)
            {
                return;// nothing being added
            }

            List<Dictionary<int, string>> invertedDictionaries = new List<Dictionary<int, string>>();
            var newNameIndexDictionary = new Dictionary<string, List<int>>();

            List<MapDrawableBatch> layers = mapDrawableBatches.ToList();
            layers.Insert(0, this);

            int[] currentVertIndex = new int[layers.Count];

            // they should be initialized to 0
            int destinationVertIndex = 0;
            int destinationIndexIndex = 0;



            var newVerts = new VertexType[totalNumberOfVerts];
            int[] new32BitIndexes = null;
            short[] new16BitIndexes = null;

            if(totalNumberOfIndexes < short.MaxValue)
            {
                new16BitIndexes = new short[totalNumberOfIndexes];
            }
            else
            {
                new32BitIndexes = new int[totalNumberOfIndexes];
            }

            mCurrentNumberOfTiles = totalNumberOfVerts / 4;

            int newFlagFlipArraySize = 0;
            foreach (var layer in layers)
            {
                newFlagFlipArraySize += layer.FlipFlagArray.Length;
                var invertedLayerDictionary = new Dictionary<int, string>();

                foreach (var kvp in layer.NamedTileOrderedIndexes)
                {
                    foreach (var index in kvp.Value)
                    {
                        invertedLayerDictionary[index] = kvp.Key;
                    }
                }

                invertedDictionaries.Add(invertedLayerDictionary);
            }

            var newFlipFlagArray = new byte[newFlagFlipArraySize];

            while (true)
            {
                float smallestX = float.PositiveInfinity;
                //int smallestIndex = -1;
                int layerIndexToCopyFrom = -1;

                for (int layerIndex = 0; layerIndex < currentVertIndex.Length; layerIndex++)
                {
                    if (currentVertIndex[layerIndex] < layers[layerIndex].mVertices.Length)
                    {
                        var vertX = layers[layerIndex].mVertices[currentVertIndex[layerIndex]].Position.X;

                        if (vertX < smallestX)
                        {
                            smallestX = vertX;
                            layerIndexToCopyFrom = layerIndex;
                            //smallestIndex = currentVertIndex[layerIndex];
                        }
                    }
                }

                if (layerIndexToCopyFrom == -1)
                {
                    break;
                }
                else
                {
                    var layerToCopyFrom = layers[layerIndexToCopyFrom];
                    var sourceVertIndex = currentVertIndex[layerIndexToCopyFrom];
                    var sourceIndexIndex = (sourceVertIndex / 4) * 6;
                    var sourceFlipIndex = (sourceVertIndex / 4);

                    var destinationFlipIndex = destinationVertIndex / 4;

                    newFlipFlagArray[destinationFlipIndex] = layerToCopyFrom.FlipFlagArray[sourceFlipIndex];

                    newVerts[destinationVertIndex] = layerToCopyFrom.mVertices[sourceVertIndex];
                    newVerts[destinationVertIndex + 1] = layerToCopyFrom.mVertices[sourceVertIndex + 1];
                    newVerts[destinationVertIndex + 2] = layerToCopyFrom.mVertices[sourceVertIndex + 2];
                    newVerts[destinationVertIndex + 3] = layerToCopyFrom.mVertices[sourceVertIndex + 3];

                    if (new32BitIndexes != null)
                    {
                        if (layerToCopyFrom.indices32Bit != null)
                        {
                            var firstVert = layerToCopyFrom.indices32Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex + 1] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 1];
                            new32BitIndexes[destinationIndexIndex + 2] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 2];
                            new32BitIndexes[destinationIndexIndex + 3] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 3];
                            new32BitIndexes[destinationIndexIndex + 4] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 4];
                            new32BitIndexes[destinationIndexIndex + 5] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 5];
                        }
                        else
                        {
                            var firstVert = layerToCopyFrom.indices16Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex + 1] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 1];
                            new32BitIndexes[destinationIndexIndex + 2] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 2];
                            new32BitIndexes[destinationIndexIndex + 3] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 3];
                            new32BitIndexes[destinationIndexIndex + 4] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 4];
                            new32BitIndexes[destinationIndexIndex + 5] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 5];
                        }
                    }
                    else
                    {
                        var firstVert = layerToCopyFrom.indices16Bit[sourceIndexIndex];

                        // assume copying from 16 bit to a 16 bit, or else this will fail anyway due to length

                        new16BitIndexes[destinationIndexIndex] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex]);
                        new16BitIndexes[destinationIndexIndex + 1] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 1]);
                        new16BitIndexes[destinationIndexIndex + 2] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 2]);
                        new16BitIndexes[destinationIndexIndex + 3] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 3]);
                        new16BitIndexes[destinationIndexIndex + 4] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 4]);
                        new16BitIndexes[destinationIndexIndex + 5] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 5]);
                    }

                    if (invertedDictionaries[layerIndexToCopyFrom].ContainsKey(sourceVertIndex / 4))
                    {
                        var newName = invertedDictionaries[layerIndexToCopyFrom][sourceVertIndex / 4];

                        if (newNameIndexDictionary.ContainsKey(newName) == false)
                        {
                            newNameIndexDictionary[newName] = new List<int>();
                        }

                        newNameIndexDictionary[newName].Add(destinationVertIndex / 4);
                    }

                    destinationVertIndex += 4;
                    destinationIndexIndex += 6;
                    currentVertIndex[layerIndexToCopyFrom] += 4;
                }
            }

            this.mNamedTileOrderedIndexes = newNameIndexDictionary;
            this.FlipFlagArray = newFlipFlagArray;

            this.mVertices = newVerts;
            this.indices32Bit = new32BitIndexes;
            this.indices16Bit = new16BitIndexes;
        }

        private void MergeSortedY(IEnumerable<MapDrawableBatch> mapDrawableBatches, int totalNumberOfVerts, int totalNumberOfIndexes)
        {
            if (totalNumberOfVerts == this.mVertices.Length)
            {
                return;// nothing being added
            }
            List<Dictionary<int, string>> invertedDictionaries = new List<Dictionary<int, string>>();
            var newNameIndexDictionary = new Dictionary<string, List<int>>();

            List<MapDrawableBatch> layers = mapDrawableBatches.ToList();
            layers.Insert(0, this);

            int[] currentVertIndex = new int[layers.Count];

            // they should be initialized to 0
            int destinationVertIndex = 0;
            int destinationIndexIndex = 0;

            var newVerts = new VertexType[totalNumberOfVerts];

            int[] new32BitIndexes = null;
            short[] new16BitIndexes = null;

            if(totalNumberOfIndexes < short.MaxValue)
            {
                new16BitIndexes = new short[totalNumberOfIndexes];
            }
            else
            {
                new32BitIndexes = new int[totalNumberOfIndexes];
            }

            mCurrentNumberOfTiles = totalNumberOfVerts / 4;

            int newFlagFlipArraySize = 0;
            foreach (var layer in layers)
            {
                newFlagFlipArraySize += layer.FlipFlagArray.Length;

                var invertedLayerDictionary = new Dictionary<int, string>();

                foreach (var kvp in layer.NamedTileOrderedIndexes)
                {
                    foreach (var index in kvp.Value)
                    {
                        invertedLayerDictionary[index] = kvp.Key;
                    }
                }

                invertedDictionaries.Add(invertedLayerDictionary);
            }

            var newFlipFlagArray = new byte[newFlagFlipArraySize];

            while (true)
            {
                float smallestY = float.PositiveInfinity;
                //int smallestIndex = -1;
                int layerIndexToCopyFrom = -1;

                for (int layerIndex = 0; layerIndex < currentVertIndex.Length; layerIndex++)
                {
                    if (currentVertIndex[layerIndex] < layers[layerIndex].mVertices.Length)
                    {
                        var vertY = layers[layerIndex].mVertices[currentVertIndex[layerIndex]].Position.Y;

                        if (vertY < smallestY)
                        {
                            smallestY = vertY;
                            layerIndexToCopyFrom = layerIndex;
                            //smallestIndex = currentVertIndex[layerIndex];
                        }
                    }
                }

                if (layerIndexToCopyFrom == -1)
                {
                    break;
                }
                else
                {
                    var layerToCopyFrom = layers[layerIndexToCopyFrom];
                    var sourceVertIndex = currentVertIndex[layerIndexToCopyFrom];
                    var sourceIndexIndex = (sourceVertIndex / 4) * 6;
                    var sourceFlipIndex = (sourceVertIndex / 4);

                    var destinationFlipIndex = destinationVertIndex / 4;

                    newFlipFlagArray[destinationFlipIndex] = layerToCopyFrom.FlipFlagArray[sourceFlipIndex];

                    newVerts[destinationVertIndex] = layerToCopyFrom.mVertices[sourceVertIndex];
                    newVerts[destinationVertIndex + 1] = layerToCopyFrom.mVertices[sourceVertIndex + 1];
                    newVerts[destinationVertIndex + 2] = layerToCopyFrom.mVertices[sourceVertIndex + 2];
                    newVerts[destinationVertIndex + 3] = layerToCopyFrom.mVertices[sourceVertIndex + 3];

                    if (new32BitIndexes != null)
                    {
                        if (layerToCopyFrom.indices32Bit != null)
                        {
                            var firstVert = layerToCopyFrom.indices32Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex + 1] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 1];
                            new32BitIndexes[destinationIndexIndex + 2] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 2];
                            new32BitIndexes[destinationIndexIndex + 3] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 3];
                            new32BitIndexes[destinationIndexIndex + 4] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 4];
                            new32BitIndexes[destinationIndexIndex + 5] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices32Bit[sourceIndexIndex + 5];
                        }
                        else
                        {
                            var firstVert = layerToCopyFrom.indices16Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex];
                            new32BitIndexes[destinationIndexIndex + 1] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 1];
                            new32BitIndexes[destinationIndexIndex + 2] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 2];
                            new32BitIndexes[destinationIndexIndex + 3] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 3];
                            new32BitIndexes[destinationIndexIndex + 4] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 4];
                            new32BitIndexes[destinationIndexIndex + 5] =
                                destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 5];
                        }
                    }
                    else
                    {
                        var firstVert = layerToCopyFrom.indices16Bit[sourceIndexIndex];

                        // assume copying from 16 bit to a 16 bit, or else this will fail anyway due to length

                        new16BitIndexes[destinationIndexIndex] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex]);
                        new16BitIndexes[destinationIndexIndex + 1] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 1]);
                        new16BitIndexes[destinationIndexIndex + 2] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 2]);
                        new16BitIndexes[destinationIndexIndex + 3] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 3]);
                        new16BitIndexes[destinationIndexIndex + 4] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 4]);
                        new16BitIndexes[destinationIndexIndex + 5] =
                            (short)(destinationVertIndex - firstVert + layerToCopyFrom.indices16Bit[sourceIndexIndex + 5]);
                    }

                    if (invertedDictionaries[layerIndexToCopyFrom].ContainsKey(sourceVertIndex / 4))
                    {
                        var newName = invertedDictionaries[layerIndexToCopyFrom][sourceVertIndex / 4];

                        if (newNameIndexDictionary.ContainsKey(newName) == false)
                        {
                            newNameIndexDictionary[newName] = new List<int>();
                        }

                        newNameIndexDictionary[newName].Add(destinationVertIndex / 4);
                    }

                    destinationVertIndex += 4;
                    destinationIndexIndex += 6;
                    currentVertIndex[layerIndexToCopyFrom] += 4;
                }
            }

            this.mNamedTileOrderedIndexes = newNameIndexDictionary;
            this.FlipFlagArray = newFlipFlagArray;

            this.mVertices = newVerts;
            this.indices32Bit = new32BitIndexes;
            this.indices16Bit = new16BitIndexes;

        }

        private void MergeUnsorted(IEnumerable<MapDrawableBatch> mapDrawableBatches, int quadsOnThis, int totalNumberOfVerts, int totalNumberOfIndexes, VertexType[] oldVerts, int[] old32BitIndexes, short[] old16BitIndexes)
        {
            mVertices = new VertexType[totalNumberOfVerts];

            if(indices32Bit != null)
            {
                indices32Bit = new int[totalNumberOfIndexes];
            }
            if(old16BitIndexes != null)
            {
                indices16Bit = new short[totalNumberOfIndexes];
            }

            oldVerts.CopyTo(mVertices, 0);
            old32BitIndexes?.CopyTo(indices32Bit, 0);
            old16BitIndexes?.CopyTo(indices16Bit, 0);   

            int currentQuadIndex = quadsOnThis;


            int index = 0;
            foreach (var mdb in mapDrawableBatches)
            {
                int startVert = currentQuadIndex * 4;
                int startIndex = currentQuadIndex * 6;
                int numberOfIndices = mdb.indices32Bit?.Length ?? mdb.indices16Bit.Length;
                int numberOfNewVertices = mdb.mVertices.Length;

                mdb.mVertices.CopyTo(mVertices, startVert);
                mdb.indices32Bit?.CopyTo(indices32Bit, startIndex);
                mdb.indices16Bit?.CopyTo(indices16Bit, startIndex);

                if(indices32Bit != null)
                {
                    for (int i = startIndex; i < startIndex + numberOfIndices; i++)
                    {
                        indices32Bit[i] += startVert;
                    }
                }
                if (indices16Bit != null)
                {
                    for (int i = startIndex; i < startIndex + numberOfIndices; i++)
                    {
                        indices16Bit[i] += (short)startVert;
                    }
                }


                for (int i = startVert; i < startVert + numberOfNewVertices; i++)
                {
                    mVertices[i].Position.Z += index + 1;
                }

                foreach (var kvp in mdb.mNamedTileOrderedIndexes)
                {
                    string key = kvp.Key;

                    List<int> toAddTo;

                    if (mNamedTileOrderedIndexes.ContainsKey(key))
                    {
                        toAddTo = mNamedTileOrderedIndexes[key];
                    }
                    else
                    {
                        toAddTo = new List<int>();
                        mNamedTileOrderedIndexes[key] = toAddTo;
                    }

                    foreach (var namedIndex in kvp.Value)
                    {
                        toAddTo.Add(namedIndex + currentQuadIndex);
                    }
                }


                currentQuadIndex += mdb.QuadCount;
                index++;
            }
        }

        public void SortQuadsOnAxis(SortAxis sortAxis)
        {
            this.SortAxis = sortAxis;

            List<Quad> quads = new List<Quad>();

            for (int i = 0; i < Vertices.Count(); i += 4)
            {
                var quad = new Quad();
                quad.Vertices[0] = Vertices[i + 0];
                quad.Vertices[1] = Vertices[i + 1];
                quad.Vertices[2] = Vertices[i + 2];
                quad.Vertices[3] = Vertices[i + 3];

                quads.Add(quad);
            }

            Quad[] sortedQuads = null;
            if (sortAxis == SortAxis.X)
            {
                sortedQuads = quads.OrderBy(item => item.Position.X).ToArray();
            }
            else if (sortAxis == SortAxis.Y)
            {
                sortedQuads = quads.OrderBy(item => item.Position.Y).ToArray();
            }
            else
            {
                throw new ArgumentException($"Invalid sort axis: {sortAxis}");
            }

            for (int i = 0; i < sortedQuads.Length; i++)
            {
                Vertices[i * 4 + 0] = sortedQuads[i].Vertices[0];
                Vertices[i * 4 + 1] = sortedQuads[i].Vertices[1];
                Vertices[i * 4 + 2] = sortedQuads[i].Vertices[2];
                Vertices[i * 4 + 3] = sortedQuads[i].Vertices[3];
            }
        }

        /// <summary>
        /// Removes quads from the TileMap using the argument QuadIndexes
        /// </summary>
        /// <param name="quadIndexes">The indexes of the quads</param>
        public void RemoveQuads(IEnumerable<int> quadIndexes)
        {
            var vertList = mVertices.ToList();
            // Reverse - go from biggest to smallest
            foreach (var indexToRemove in quadIndexes.Distinct().OrderBy(item => -item))
            {
                // and go from biggest to smallest here too
                vertList.RemoveAt(indexToRemove * 4 + 3);
                vertList.RemoveAt(indexToRemove * 4 + 2);
                vertList.RemoveAt(indexToRemove * 4 + 1);
                vertList.RemoveAt(indexToRemove * 4 + 0);
            }

            mVertices = vertList.ToArray();

            // The mNamedTileOrderedIndexes is a dictionary that stores which indexes are stored
            // with which tiles.  For example, the key in the dictionary may be "Lava", in which case
            // the value is the indexes of the tiles that use the Lava tile.
            // If we do end up removing any quads, then all following quads will shift, so we need to
            // adjust the indexes so the naming works correctly

            List<int> orderedInts = quadIndexes.OrderBy(item => item).Distinct().ToList();
            int numberOfRemovals = 0;
            foreach (var kvp in mNamedTileOrderedIndexes)
            {
                var ints = kvp.Value;

                numberOfRemovals = 0;

                for (int i = 0; i < ints.Count; i++)
                {
                    // Nothing left to test, so subtract and move on....
                    if (numberOfRemovals == orderedInts.Count)
                    {
                        ints[i] -= numberOfRemovals;
                    }
                    else if (ints[i] == orderedInts[numberOfRemovals])
                    {
                        ints.Clear();
                        break;
                    }
                    else if (ints[i] < orderedInts[numberOfRemovals])
                    {
                        ints[i] -= numberOfRemovals;
                    }
                    else
                    {
                        while (numberOfRemovals < orderedInts.Count && ints[i] > orderedInts[numberOfRemovals])
                        {
                            numberOfRemovals++;
                        }
                        if (numberOfRemovals < orderedInts.Count && ints[i] == orderedInts[numberOfRemovals])
                        {
                            ints.Clear();
                            break;
                        }

                        ints[i] -= numberOfRemovals;
                    }
                }
            }
        }

        #endregion
    }

    #region Additional Classes

    public class Quad
    {
        public Vector3 Position => Vertices[0].Position;

        public VertexType[] Vertices = new VertexType[4];
    }

    public static class MapDrawableBatchExtensionMethods
    {


    }

    #endregion
}
