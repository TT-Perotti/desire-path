using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using ProtoBuf; // <— needed for [ProtoContract]

namespace DesirePaths
{
    [ProtoContract]
    public class PathWear
    {
        [ProtoMember(1)]
        public int WearLevel { get; set; }

        [ProtoMember(2)]
        public double LastUpdateHours { get; set; }

        [ProtoMember(3)]
        public string OriginalBlockCode { get; set; }
    }

    public class DesirePathModSystem : ModSystem
    {
        public static DesirePathModSystem Instance;

        ICoreServerAPI sapi;

        private List<BlockPos> pendingEffects = new List<BlockPos>();

        // Track wear per block coordinate
        public Dictionary<BlockPos, PathWear> pathWearMap = new Dictionary<BlockPos, PathWear>();

        // thresholds
        public int plantKillThreshold = 5;
        public int dirtPathThreshold = 20;

        // decay ticker
        int decayIntervalMs = 5000; // every 5s
        long decayTickerId = 0;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            Instance = this;

            // Register a periodic tick listener (use RegisterGameTickListener rather than an OnServerTick override)
            decayTickerId = sapi.Event.RegisterGameTickListener(OnDecayTick, decayIntervalMs);

            // Hook players joining so we can attach the behavior
            sapi.Event.PlayerJoin += OnPlayerJoin;

            // Attach behavior to already-online players (if any)
            foreach (var sp in sapi.World.AllOnlinePlayers)
            {
                if (sp?.Entity != null)
                {
                    sp.Entity.AddBehavior(new PlayerBehaviorDesirePath(sp.Entity));
                }
            }

            // Save/load handlers
            sapi.Event.SaveGameLoaded += OnSaveGameLoaded;
            sapi.Event.GameWorldSave += OnSaveGameSaving;
        }

        private void OnPlayerJoin(IServerPlayer byPlayer)
        {
            if (byPlayer?.Entity != null)
            {
                byPlayer.Entity.AddBehavior(new PlayerBehaviorDesirePath(byPlayer.Entity));
            }
        }

        // Called by the registered tick listener every decayIntervalMs
        private void OnDecayTick(float dt)
        {
            double now = sapi.World.Calendar.TotalHours;
            DecayWear(now);
        }

        public void AddWear(BlockPos pos)
        {
            double now = sapi.World.Calendar.TotalHours;

            if (!pathWearMap.TryGetValue(pos, out PathWear wear))
            {
                wear = new PathWear { 
                    WearLevel = 0,
                    LastUpdateHours = now,
                    OriginalBlockCode = sapi.World.BlockAccessor.GetBlock(pos).Code.ToString()
                };
                pathWearMap[pos] = wear;
            }

            wear.WearLevel++;
            wear.LastUpdateHours = now;

            if (!pendingEffects.Contains(pos))
            {
                pendingEffects.Add(pos);
            }

            // ApplyPathEffect(pos, wear.WearLevel);
            sapi.Logger.Notification($"[DesirePaths] Player stepped on {pos} wear={wear.WearLevel}");
        }

        private void ApplyPathEffect(BlockPos pos, int wearLevel)
        {
            Block groundBlock = sapi.World.BlockAccessor.GetBlock(pos);      // dirt underfoot

            // Change the ground block itself to dirt path at higher threshold
            if (wearLevel >= dirtPathThreshold)
            {
                Block pathBlock = sapi.World.GetBlock(new AssetLocation("desire-paths", "packeddirt-path"));
                bool groundLooksLikeSoil =
                   groundBlock.BlockMaterial == EnumBlockMaterial.Soil ||
                   groundBlock.Code.Path.Contains("soil") ||
                   groundBlock.Code.Path.Contains("dirt") ||
                   groundBlock.Code.Path.Contains("grass");
                if (pathBlock != null && groundLooksLikeSoil) // check you’re on dirt
                {
                    sapi.World.BlockAccessor.ExchangeBlock(pathBlock.BlockId, pos);
                }
            }
        }


        private void DecayWear(double now)
        {
            List<BlockPos> toRemove = new List<BlockPos>();

            foreach (var kvp in pathWearMap)
            {
                var wear = kvp.Value;
                BlockPos pos = kvp.Key;

                // If not walked on for 2 days, decay by 1
                if (now - wear.LastUpdateHours > 48.0)
                {
                    wear.WearLevel--;
                    // avoid repeatedly decaying every tick: update LastUpdateHours to now so decay happens gradually
                    wear.LastUpdateHours = now;
                }

                BlockPos up = pos.UpCopy();
                Block aboveBlock = sapi.World.BlockAccessor.GetBlock(up);
                if (wear.WearLevel >= plantKillThreshold && aboveBlock.BlockMaterial == EnumBlockMaterial.Plant)
                {
                    sapi.World.BlockAccessor.SetBlock(0, up); // remove plant above dirt
                }

                if (wear.WearLevel <= 0) {
                    if (!string.IsNullOrEmpty(wear.OriginalBlockCode))
                    {
                        AssetLocation origLoc = new AssetLocation(wear.OriginalBlockCode);
                        Block origBlock = sapi.World.GetBlock(origLoc);
                        if (origBlock != null)
                        {
                            // Replace the path block with the original block
                            sapi.World.BlockAccessor.ExchangeBlock(origBlock.BlockId, pos);
                            sapi.Logger.Notification($"[DesirePaths] Reverting {pos} back to {origBlock.Code}");
                        }
                    }
                    toRemove.Add(pos);
                    toRemove.Add(kvp.Key);
                };
            }

            foreach (var pos in toRemove) pathWearMap.Remove(pos);
        }

        public void CheckPendingEffectsForPlayer(BlockPos playerPos)
        {
            // copy list to avoid modification while iterating
            var toCheck = new List<BlockPos>(pendingEffects);

            foreach (BlockPos pos in toCheck)
            {
                double dx = playerPos.X - pos.X;
                double dy = playerPos.Y - pos.Y;
                double dz = playerPos.Z - pos.Z;
                double distSq = dx * dx + dy * dy + dz * dz;

                if (distSq >= 30 * 30)
                {
                    // Player is far enough from this pos
                    if (pathWearMap.TryGetValue(pos, out PathWear wear))
                    {
                        ApplyPathEffect(pos, wear.WearLevel);
                    }
                    pendingEffects.Remove(pos);
                }
            }
        }


        #region Save/Load

        private void OnSaveGameLoaded()
        {
            var data = sapi.WorldManager.SaveGame.GetData("desirepaths");
            if (data != null)
            {
                pathWearMap = SerializerUtil.Deserialize<Dictionary<BlockPos, PathWear>>(data);
                sapi.Logger.Notification("DesirePaths: Loaded {0} path entries", pathWearMap.Count);
            }
        }

        private void OnSaveGameSaving()
        {
            var data = SerializerUtil.Serialize(pathWearMap);
            sapi.WorldManager.SaveGame.StoreData("desirepaths", data);
            sapi.Logger.Notification("DesirePaths: Saved {0} path entries", pathWearMap.Count);
        }

        #endregion
    }

    // Per-player behavior that detects stepping onto a new block
    public class PlayerBehaviorDesirePath : EntityBehavior
    {
        BlockPos lastBlockPos;

        public PlayerBehaviorDesirePath(Entity entity) : base(entity)
        {
            lastBlockPos = new BlockPos((int)entity.Pos.X, (int)entity.Pos.Y - 1, (int)entity.Pos.Z);
        }

        public override void OnGameTick(float deltaTime)
        {
            base.OnGameTick(deltaTime);
            BlockPos currentPos = new BlockPos((int)entity.Pos.X, (int)entity.Pos.Y - 1, (int)entity.Pos.Z);

            if (!currentPos.Equals(lastBlockPos))
            {
                lastBlockPos = currentPos;
                if (DesirePathModSystem.Instance != null)
                {
                    DesirePathModSystem.Instance.AddWear(currentPos);
                    DesirePathModSystem.Instance.CheckPendingEffectsForPlayer(entity.Pos.AsBlockPos);
                }
            }
        }

        public override string PropertyName() => "desirepathbehavior";
    }
}
