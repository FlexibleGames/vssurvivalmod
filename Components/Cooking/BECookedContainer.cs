﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace Vintagestory.GameContent
{
    public class BlockEntityCookedContainer : BlockEntityContainer, IBlockShapeSupplier
    {
        public override InventoryBase Inventory => inventory;
        public override string InventoryClassName => "cookedcontainer";


        internal InventoryGeneric inventory;
        public float QuantityServings;
        public string RecipeCode;

        internal BlockCookedContainer ownBlock;

        MeshData currentMesh;

        bool wasRotten;
        int tickCnt = 0;

        public bool Rotten
        {
            get
            {
                bool rotten = false;
                for (int i = 0; i < inventory.Count; i++)
                {
                    rotten |= inventory[i].Itemstack?.Collectible.Code.Path == "rot";
                }

                return rotten;
            }
        }

        public CookingRecipe FromRecipe
        {
            get { return api.World.CookingRecipes.FirstOrDefault(rec => rec.Code == RecipeCode); }
        }

        public BlockEntityCookedContainer()
        {
            inventory = new InventoryGeneric(4, null, null);
        }

        public override void Initialize(ICoreAPI api)
        {
            base.Initialize(api);

            ownBlock = api.World.BlockAccessor.GetBlock(pos) as BlockCookedContainer;

            if (api.Side == EnumAppSide.Client)
            {
                RegisterGameTickListener(Every100ms, 200);
            }

            if (api.Side == EnumAppSide.Client && currentMesh == null)
            {
                currentMesh = GenMesh();
                MarkDirty(true);
            }
        }

        private void Every100ms(float dt)
        {
            float temp = GetTemperature();
            if (api.World.Rand.NextDouble() < (temp - 50) / 160)
            {
                BlockCookedContainer.smokeHeld.minPos = pos.ToVec3d().AddCopy(0.5-0.05, 0.3125, 0.5-0.05);
                api.World.SpawnParticles(BlockCookedContainer.smokeHeld);
            }

            if (tickCnt++ % 20 == 0)
            {
                if (!wasRotten && Rotten)
                {
                    currentMesh = GenMesh();
                    MarkDirty(true);
                    wasRotten = true;
                }
            }
        }


        private int GetTemperature()
        {
            ItemStack[] stacks = GetNonEmptyContentStacks(false);
            if (stacks.Length == 0 || stacks[0] == null) return 0;

            return (int)stacks[0].Collectible.GetTemperature(api.World, stacks[0]);
        }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            BlockCookedContainer blockpot = byItemStack?.Block as BlockCookedContainer;
            if (blockpot != null)
            {
                TreeAttribute tempTree = byItemStack.Attributes?["temperature"] as TreeAttribute;

                ItemStack[] stacks = blockpot.GetContents(api.World, byItemStack);
                for (int i = 0; i < stacks.Length; i++)
                {
                    ItemStack stack = stacks[i].Clone();
                    Inventory[i].Itemstack = stack;

                    // Clone temp attribute    
                    if (tempTree != null) stack.Attributes["temperature"] = tempTree.Clone();
                }

                RecipeCode = blockpot.GetRecipeCode(api.World, byItemStack);
                QuantityServings = blockpot.GetServings(api.World, byItemStack);
            }

            if (api.Side == EnumAppSide.Client)
            {
                currentMesh = GenMesh();
                MarkDirty(true);
            }
        }


        public override void OnBlockBroken()
        {
            // Don't drop contents
        }

        public override void FromTreeAtributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
        {
            base.FromTreeAtributes(tree, worldForResolving);

            QuantityServings = (float)tree.GetDecimal("quantityServings", 1);
            RecipeCode = tree.GetString("recipeCode");

            if (api?.Side == EnumAppSide.Client && currentMesh == null)
            {
                currentMesh = GenMesh();
                MarkDirty(true);
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetFloat("quantityServings", QuantityServings);
            tree.SetString("recipeCode", RecipeCode == null ? "" : RecipeCode);
        }

        public bool ServeInto(IPlayer player, ItemSlot slot)
        {
            int capacity = slot.Itemstack.Collectible.Attributes["servingCapacity"].AsInt();
            float servings = Math.Min(QuantityServings, capacity);

            ItemStack mealStack=null;
            IBlockMealContainer ibm = (slot.Itemstack.Collectible as IBlockMealContainer);

            if (ibm != null && ibm.GetQuantityServings(api.World, slot.Itemstack) > 0)
            {
                float existingServings = ibm.GetQuantityServings(api.World, slot.Itemstack);
                //string recipeCode = ibm.GetRecipeCode(api.World, slot.Itemstack);
                ItemStack[] existingContent = ibm.GetNonEmptyContents(api.World, slot.Itemstack);

                servings = Math.Min(servings, capacity - existingServings);
                ItemStack[] potStacks = GetNonEmptyContentStacks();

                if (servings == 0) return false;
                if (existingContent.Length != potStacks.Length) return false;
                for (int i = 0; i < existingContent.Length; i++)
                {
                    if (!existingContent[i].Equals(api.World, potStacks[i], GlobalConstants.IgnoredStackAttributes))
                    {
                        return false;
                    }
                }

                if (slot.StackSize == 1)
                {
                    mealStack = slot.Itemstack;
                    ibm.SetContents(RecipeCode, slot.Itemstack, GetNonEmptyContentStacks(), existingServings + servings);
                } else
                {
                    mealStack = slot.Itemstack.Clone();
                    ibm.SetContents(RecipeCode, mealStack, GetNonEmptyContentStacks(), existingServings + servings);
                }
            }
            else
            {
                mealStack = new ItemStack(api.World.GetBlock(AssetLocation.Create(slot.Itemstack.Collectible.Attributes["mealBlockCode"].AsString(), slot.Itemstack.Collectible.Code.Domain)));
                mealStack.StackSize = 1;
                (mealStack.Collectible as IBlockMealContainer).SetContents(RecipeCode, mealStack, GetNonEmptyContentStacks(), servings);
            }


            if (slot.StackSize == 1)
            {
                slot.Itemstack = mealStack;
            }
            else
            {
                slot.TakeOut(1);
                if (!player.InventoryManager.TryGiveItemstack(mealStack, true))
                {
                    api.World.SpawnItemEntity(mealStack, pos.ToVec3d().Add(0.5, 0.5, 0.5));
                }
                slot.MarkDirty();
            }

            QuantityServings -= servings;

            if (QuantityServings <= 0)
            {
                Block block = api.World.GetBlock(ownBlock.CodeWithPath(ownBlock.FirstCodePart() + "-burned"));
                api.World.BlockAccessor.SetBlock(block.BlockId, pos);
                return true;
            }

            if (api.Side == EnumAppSide.Client)
            {
                currentMesh = GenMesh();
                (player as IClientPlayer).TriggerFpAnimation(EnumHandInteract.HeldItemAttack);
            }
            
            MarkDirty(true);
            return true;
        }

        public MeshData GenMesh()
        {
            if (ownBlock == null) return null;
            ItemStack[] stacks = GetNonEmptyContentStacks();
            if (stacks == null || stacks.Length == 0) return null;

            ICoreClientAPI capi = api as ICoreClientAPI;
            return capi.ModLoader.GetModSystem<MealMeshCache>().CreateMealMesh(ownBlock.Shape, FromRecipe, stacks, new Vec3f(0, 2.5f/16f, 0));
        }


        public bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
        {
            mesher.AddMeshData(currentMesh);
            return true;
        }


        public override string GetBlockInfo(IPlayer forPlayer)
        {
            ItemStack[] contentStacks = GetNonEmptyContentStacks();
            CookingRecipe recipe = api.World.CookingRecipes.FirstOrDefault(rec => rec.Code == RecipeCode);
            if (recipe == null) return null;

            float servings = QuantityServings;
            int temp = GetTemperature();
            string temppretty = Lang.Get("{0}°C", temp);
            if (temp < 20) temppretty = "Cold";
    
            BlockMeal mealblock = api.World.GetBlock(new AssetLocation("bowl-meal")) as BlockMeal;
            string nutriFacts = mealblock.GetContentNutritionFacts(api.World, inventory[0], contentStacks, forPlayer.Entity);

            StringBuilder dsc = new StringBuilder();

            if (servings == 1)
            {
                dsc.Append(Lang.Get("{0} serving of {1}\nTemperature: {2}{3}{4}", Math.Round(servings, 1), recipe.GetOutputName(forPlayer.Entity.World, contentStacks), temppretty, nutriFacts != null ? "\n" : "", nutriFacts));
            }
            else
            {
                dsc.Append(Lang.Get("{0} servings of {1}\nTemperature: {2}{3}{4}", Math.Round(servings, 1), recipe.GetOutputName(forPlayer.Entity.World, contentStacks), temppretty, nutriFacts != null ? "\n" : "", nutriFacts));
            }


            foreach (var slot in inventory)
            {
                if (slot.Empty) continue;

                TransitionableProperties[] propsm = slot.Itemstack.Collectible.GetTransitionableProperties(api.World, slot.Itemstack, null);
                if (propsm != null && propsm.Length > 0)
                {
                    slot.Itemstack.Collectible.AppendPerishableInfoText(slot, dsc, api.World);
                    break;
                }
            }


            return dsc.ToString();
        }
        
    }
}