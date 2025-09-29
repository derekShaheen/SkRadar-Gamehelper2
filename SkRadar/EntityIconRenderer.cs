namespace Radar
{
    using System.Numerics;
    using GameHelper;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteEnums.Entity;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.Utils;
    using ImGuiNET;

    internal sealed class EntityIconRenderer
    {
        private readonly Radar owner;

        internal EntityIconRenderer(Radar owner)
        {
            this.owner = owner;
        }

        internal void Draw(Vector2 mapCenter, float iconSizeMultiplier)
        {
            var fgDraw = ImGui.GetWindowDrawList();
            var currentAreaInstance = Core.States.InGameStateObject.CurrentAreaInstance;
            if (!currentAreaInstance.Player.TryGetComponent<Render>(out var playerRender))
                return;

            var playerPosition = new Vector2(playerRender.GridPosition.X, playerRender.GridPosition.Y);
            foreach (var entity in currentAreaInstance.AwakeEntities)
            {
                if (this.owner.Settings.HideOutsideNetworkBubble && !entity.Value.IsValid)
                    continue;

                if (entity.Value.EntityState == EntityStates.Useless)
                    continue;

                if (!entity.Value.TryGetComponent<Render>(out var entityRender))
                    continue;

                var entityPosition = new Vector2(entityRender.GridPosition.X, entityRender.GridPosition.Y);
                var screenDelta = Helper.DeltaInWorldToMapDelta(entityPosition - playerPosition, entityRender.TerrainHeight - playerRender.TerrainHeight);
                var iconSize = Vector2.One * iconSizeMultiplier;

                switch (entity.Value.EntityType)
                {
                    case EntityTypes.NPC:
                    {
                        var npcIcon = entity.Value.EntitySubtype switch
                        {
                            EntitySubtypes.SpecialNPC => this.owner.Settings.BaseIcons["Special NPC"],
                            _ => this.owner.Settings.BaseIcons["NPC"],
                        };
                        iconSize *= npcIcon.IconScale;
                        fgDraw.AddImage(
                            npcIcon.TexturePtr,
                            mapCenter + screenDelta - iconSize,
                            mapCenter + screenDelta + iconSize,
                            npcIcon.UV0,
                            npcIcon.UV1);
                        break;
                    }

                    case EntityTypes.Player:
                        if (entity.Value.EntitySubtype == EntitySubtypes.PlayerOther)
                        {
                            entity.Value.TryGetComponent<Player>(out var playerComp);
                            if (this.owner.Settings.ShowPlayersNames)
                            {
                                var nameSizeHalf = ImGui.CalcTextSize(playerComp.Name) / 2;
                                fgDraw.AddRectFilled(mapCenter + screenDelta - nameSizeHalf, mapCenter + screenDelta + nameSizeHalf,
                                    ImGuiHelper.Color(0, 0, 0, 200));
                                fgDraw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), mapCenter + screenDelta - nameSizeHalf,
                                    ImGuiHelper.Color(255, 128, 128, 255), playerComp.Name);
                            }
                            else
                            {
                                var playerIcon = entity.Value.EntityState == EntityStates.PlayerLeader
                                    ? this.owner.Settings.BaseIcons["Leader"]
                                    : this.owner.Settings.BaseIcons["Player"];
                                iconSize *= playerIcon.IconScale;
                                fgDraw.AddImage(
                                    playerIcon.TexturePtr,
                                    mapCenter + screenDelta - iconSize,
                                    mapCenter + screenDelta + iconSize,
                                    playerIcon.UV0,
                                    playerIcon.UV1);
                            }
                        }
                        else
                        {
                            var selfIcon = this.owner.Settings.BaseIcons["Self"];
                            iconSize *= selfIcon.IconScale;
                            fgDraw.AddImage(
                                selfIcon.TexturePtr,
                                mapCenter + screenDelta - iconSize,
                                mapCenter + screenDelta + iconSize,
                                selfIcon.UV0,
                                selfIcon.UV1);
                        }
                        break;

                    case EntityTypes.Chest:
                    {
                        IconPicker chestIcon = entity.Value.EntitySubtype switch
                        {
                            EntitySubtypes.None => this.owner.Settings.BaseIcons["All Other Chest"],
                            EntitySubtypes.ChestWithRareRarity => this.owner.Settings.BaseIcons["Rare Chests"],
                            EntitySubtypes.ChestWithMagicRarity => this.owner.Settings.BaseIcons["Magic Chests"],
                            EntitySubtypes.ExpeditionChest => this.owner.Settings.ExpeditionIcons["Generic Expedition Chests"],
                            EntitySubtypes.BreachChest => this.owner.Settings.BreachIcons["Breach Chest"],
                            EntitySubtypes.Strongbox => this.owner.Settings.BaseIcons["Strongbox"],
                            _ => this.owner.Settings.BaseIcons["All Other Chest"],
                        };
                        iconSize *= chestIcon.IconScale;
                        fgDraw.AddImage(
                            chestIcon.TexturePtr,
                            mapCenter + screenDelta - iconSize,
                            mapCenter + screenDelta + iconSize,
                            chestIcon.UV0,
                            chestIcon.UV1);
                        break;
                    }

                    case EntityTypes.Shrine:
                        if ((entity.Value.TryGetComponent<Shrine>(out var shrineComp) && shrineComp.IsUsed) ||
                            (entity.Value.TryGetComponent<Targetable>(out var targ) && !targ.IsTargetable))
                        {
                            break;
                        }
                        {
                            var shrineIcon = this.owner.Settings.BaseIcons["Shrine"];
                            iconSize *= shrineIcon.IconScale;
                            fgDraw.AddImage(
                                shrineIcon.TexturePtr,
                                mapCenter + screenDelta - iconSize,
                                mapCenter + screenDelta + iconSize,
                                shrineIcon.UV0,
                                shrineIcon.UV1);
                        }
                        break;

                    case EntityTypes.Monster:
                        switch (entity.Value.EntityState)
                        {
                            case EntityStates.None:
                                if (entity.Value.EntitySubtype == EntitySubtypes.POIMonster)
                                {
                                    if (!this.owner.Settings.POIMonsters.TryGetValue(entity.Value.EntityCustomGroup, out var poiIcon))
                                        poiIcon = this.owner.Settings.POIMonsters[-1];

                                    iconSize *= poiIcon.IconScale;
                                    fgDraw.AddImage(
                                        poiIcon.TexturePtr,
                                        mapCenter + screenDelta - iconSize,
                                        mapCenter + screenDelta + iconSize,
                                        poiIcon.UV0,
                                        poiIcon.UV1);
                                }
                                else if (entity.Value.TryGetComponent<ObjectMagicProperties>(out var omp))
                                {
                                    var monsterIcon = this.RarityToIconMapping(omp.Rarity);
                                    iconSize *= monsterIcon.IconScale;
                                    fgDraw.AddImage(
                                        monsterIcon.TexturePtr,
                                        mapCenter + screenDelta - iconSize,
                                        mapCenter + screenDelta + iconSize,
                                        monsterIcon.UV0,
                                        monsterIcon.UV1);
                                }
                                break;

                            case EntityStates.PinnacleBossHidden:
                            {
                                var bossNotAttackingIcon = this.owner.Settings.BaseIcons["Pinnacle Boss Not Attackable"];
                                iconSize *= bossNotAttackingIcon.IconScale;
                                fgDraw.AddImage(
                                    bossNotAttackingIcon.TexturePtr,
                                    mapCenter + screenDelta - iconSize,
                                    mapCenter + screenDelta + iconSize,
                                    bossNotAttackingIcon.UV0,
                                    bossNotAttackingIcon.UV1);
                                break;
                            }

                            case EntityStates.MonsterFriendly:
                            {
                                var friendlyIcon = this.owner.Settings.BaseIcons["Friendly"];
                                iconSize *= friendlyIcon.IconScale;
                                fgDraw.AddImage(
                                    friendlyIcon.TexturePtr,
                                    mapCenter + screenDelta - iconSize,
                                    mapCenter + screenDelta + iconSize,
                                    friendlyIcon.UV0,
                                    friendlyIcon.UV1);
                                break;
                            }
                        }
                        break;

                    case EntityTypes.DeliriumBomb:
                    {
                        var bombIcon = this.owner.Settings.DeliriumIcons["Delirium Bomb"];
                        iconSize *= bombIcon.IconScale;
                        fgDraw.AddImage(
                            bombIcon.TexturePtr,
                            mapCenter + screenDelta - iconSize,
                            mapCenter + screenDelta + iconSize,
                            bombIcon.UV0,
                            bombIcon.UV1);
                        break;
                    }

                    case EntityTypes.DeliriumSpawner:
                    {
                        var spawnerIcon = this.owner.Settings.DeliriumIcons["Delirium Spawner"];
                        iconSize *= spawnerIcon.IconScale;
                        fgDraw.AddImage(
                            spawnerIcon.TexturePtr,
                            mapCenter + screenDelta - iconSize,
                            mapCenter + screenDelta + iconSize,
                            spawnerIcon.UV0,
                            spawnerIcon.UV1);
                        break;
                    }

                    case EntityTypes.OtherImportantObjects:
                    {
                        if (!this.owner.Settings.OtherImportantObjects.TryGetValue(entity.Value.EntityCustomGroup, out var poiIcon))
                            poiIcon = this.owner.Settings.OtherImportantObjects[-1];

                        iconSize *= poiIcon.IconScale;
                        fgDraw.AddImage(
                            poiIcon.TexturePtr,
                            mapCenter + screenDelta - iconSize,
                            mapCenter + screenDelta + iconSize,
                            poiIcon.UV0,
                            poiIcon.UV1);
                        break;
                    }

                    case EntityTypes.Renderable:
                        fgDraw.AddCircleFilled(mapCenter + screenDelta, 3f, 0xFFFFFFFF);
                        break;
                }
            }
        }

        private IconPicker RarityToIconMapping(Rarity rarity)
        {
            return rarity switch
            {
                Rarity.Normal or Rarity.Magic or Rarity.Rare or Rarity.Unique => this.owner.Settings.BaseIcons[$"{rarity} Monster"],
                _ => this.owner.Settings.BaseIcons["Normal Monster"],
            };
        }
    }
}

