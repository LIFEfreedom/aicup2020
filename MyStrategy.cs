using Aicup2020.Model;

using System;
using System.Collections.Generic;
using System.Linq;

using Action = Aicup2020.Model.Action;

namespace Aicup2020
{
	public class MyStrategy
	{
		private readonly EntityType[] _emptyEntityTypes = Array.Empty<EntityType>();
		private readonly EntityType[] _resourceEntityTypes = new EntityType[1] { EntityType.Resource };

		private Player _selfInfo;

		private int _lostResources;

		// Player info
		private readonly List<Entity> myEntities = new List<Entity>(10);
		private readonly List<Entity> builders = new List<Entity>(10);
		private readonly List<Entity> rangedUnits = new List<Entity>(10);
		private readonly List<Entity> meleeUnits = new List<Entity>(10);
		private readonly List<Entity> houses = new List<Entity>(10);
		private readonly List<Entity> builderBases = new List<Entity>(1);
		private readonly List<Entity> rangedBases = new List<Entity>(1);
		private readonly List<Entity> meleeBases = new List<Entity>(1);

		private readonly List<Entity> resources = new List<Entity>(1000);
		private readonly List<Entity> otherEntities = new List<Entity>(100);

		private readonly Dictionary<int, EntityAction> _entityActions = new Dictionary<int, EntityAction>(10);

		private readonly SortedDictionary<int, EntityAction> _repairsActions = new();
		private readonly SortedDictionary<int, EntityAction> _buildersActions = new();

		private readonly bool[][] _cells = new bool[80][];

		private readonly int _countBuilderBasesInPrevTick = 0;
		private readonly int _countRangeBasesInPrevTick = 0;
		private readonly int _countMeleeBasesInPrevTick = 0;
		private readonly int _countHousesInPrevTick = 0;

		private int _totalFood = 0;

		public MyStrategy()
		{
			for (int i = 0; i < 80; i++)
			{
				_cells[i] = new bool[80];
			}
		}

		public Action GetAction(PlayerView playerView, DebugInterface debugInterface)
		{
			_entityActions.Clear();

			Action result = new Action(_entityActions);

			foreach (Player player in playerView.Players)
			{
				if (player.Id == playerView.MyId)
				{
					_selfInfo = player;
					_lostResources = player.Resource;
					break;
				}
			}

			ClearLists();

			CollectEntities(playerView);

			EntityProperties houseProperties = playerView.EntityProperties[EntityType.House];
			EntityProperties builderBaseProperties = playerView.EntityProperties[EntityType.BuilderBase];
			EntityProperties rangedBaseProperties = playerView.EntityProperties[EntityType.RangedBase];
			EntityProperties meleeBaseProperties = playerView.EntityProperties[EntityType.MeleeBase];

			// TODO: посчитать количество потребляемой еды используя PopulationProvide

			int consumedFood = builders.Count + rangedUnits.Count + meleeUnits.Count;
			_totalFood = builderBases.Count * builderBaseProperties.PopulationProvide
						+ rangedBases.Count * rangedBaseProperties.PopulationProvide
						+ meleeBases.Count * meleeBaseProperties.PopulationProvide
						+ houses.Count * houseProperties.PopulationProvide;

			bool needHouse = _totalFood - consumedFood < 5;

			bool newHouseBuilding = false;

			myEntities.ForEach(myEntity =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				EntityType[] validAutoAttackTargets = _emptyEntityTypes;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = playerView.EntityProperties[myEntity.EntityType];

				if (myEntity.EntityType == EntityType.BuilderUnit)
				{
					if (_repairsActions.TryGetValue(myEntity.Id, out EntityAction entityRepairAction))
					{
						if (!houses.Any() || houses.First(q => q.Id == entityRepairAction.RepairAction.Value.Target).Health == houseProperties.MaxHealth)
						{
							_repairsActions.Remove(myEntity.Id);
						}
						else
						{
							result.EntityActions[myEntity.Id] = entityRepairAction;
							return;
						}
					}

					if (_buildersActions.TryGetValue(myEntity.Id, out EntityAction entityBuildAction))
					{
						if (houses.Count > 0)
						{
							EntityType neededBuildType = entityBuildAction.BuildAction.Value.EntityType;
							//List<Entity> neededHouses = houses.Where(q => q.EntityType == neededBuildType).ToList();

							int neededHousesCountInPrevTick = neededBuildType switch
							{
								EntityType.BuilderBase => _countBuilderBasesInPrevTick,
								EntityType.RangedBase => _countRangeBasesInPrevTick,
								EntityType.MeleeBase => _countMeleeBasesInPrevTick,
								EntityType.House => _countHousesInPrevTick
							};

							if (houses.Count > neededHousesCountInPrevTick)
							{
								_buildersActions.Remove(myEntity.Id);

								Entity lastHouse = houses.OrderByDescending(q => q.Id).First();
								if (lastHouse.Health < houseProperties.MaxHealth)
								{
									repairAction = new RepairAction(lastHouse.Id);
									entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
									_repairsActions.Add(myEntity.Id, entityAction.Value);
								}
							}
							else
							{
								result.EntityActions[myEntity.Id] = entityBuildAction;
								return;
							}
						}
						else
						{
							result.EntityActions[myEntity.Id] = entityBuildAction;
							return;
						}
					}

					if (needHouse && !newHouseBuilding && _lostResources >= houseProperties.InitialCost && GetFreeLocation(houseProperties.Size, out Vec2Int positon))
					{
						validAutoAttackTargets = _emptyEntityTypes;
						moveAction = new MoveAction(
							new Vec2Int(positon.X - 1, positon.Y - 1),
							true,
							true
						);
						buildAction = new BuildAction(EntityType.House, positon);

						entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
						_buildersActions.Add(myEntity.Id, entityAction.Value);
						newHouseBuilding = true;
					}
					else
					{
						validAutoAttackTargets = _resourceEntityTypes;
						attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));
					}
				}
				else if (!needHouse && myEntity.EntityType is EntityType.BuilderBase or EntityType.RangedBase or EntityType.MeleeBase)
				{
					buildAction = BuildAction(ref playerView, ref myEntity, ref entityProperties, myEntities);
				}
				else if (myEntity.EntityType is EntityType.MeleeUnit or EntityType.RangedUnit)
				{
					moveAction = new MoveAction(
								new Vec2Int(playerView.MapSize - 1, playerView.MapSize - 1),
								true,
								true
							);
				}

				result.EntityActions[myEntity.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			return result;
		}

		private bool GetFreeLocation(int size, out Vec2Int position)
		{
			for (int num = 1; num < 80; num++)
			{
				for (int x = 1; x < num; x++)
				{
					for (int y = 1; y < num; y++)
					{
						bool notFree = false;
						for (int xi = x; xi <= x + size; xi++)
						{
							for (int yi = y; yi <= y + size; yi++)
							{
								if (_cells[xi][yi])
								{
									notFree = true;
									break;
								}
							}

							if (notFree)
							{
								break;
							}
						}

						if (!notFree)
						{
							position = new Vec2Int(x, y);
							return true;
						}
					}
				}
			}

			position = new Vec2Int(0, 0);
			return false;
		}

		private void CollectEntities(PlayerView playerView)
		{
			int entitiesLenght = playerView.Entities.Length;
			for (int i = 0; i < entitiesLenght; i++)
			{
				Entity entity = playerView.Entities[i];

				_cells[entity.Position.X][entity.Position.Y] = true;

				if (entity.PlayerId == _selfInfo.Id)
				{
					myEntities.Add(entity);
					switch (entity.EntityType)
					{
						case EntityType.BuilderBase:
							builderBases.Add(entity);
							break;
						case EntityType.BuilderUnit:
							builders.Add(entity);
							break;
						case EntityType.RangedBase:
							rangedBases.Add(entity);
							break;
						case EntityType.RangedUnit:
							rangedUnits.Add(entity);
							break;
						case EntityType.MeleeBase:
							meleeBases.Add(entity);
							break;
						case EntityType.MeleeUnit:
							meleeUnits.Add(entity);
							break;
						case EntityType.House:
							houses.Add(entity);
							break;
					}
				}
				else if (entity.EntityType == EntityType.Resource)
				{
					resources.Add(entity);
				}
				else
				{
					otherEntities.Add(entity);
				}
			}
		}

		private void ClearLists()
		{
			myEntities.Clear();
			resources.Clear();
			otherEntities.Clear();

			builders.Clear();
			rangedUnits.Clear();
			meleeUnits.Clear();
			builderBases.Clear();
			rangedBases.Clear();
			meleeBases.Clear();
			houses.Clear();

			for (int i = 0; i < 80; i++)
			{
				for (int k = 0; k < 80; k++)
				{
					_cells[i][k] = false;
				}
			}
		}

		private BuildAction? BuildAction(ref PlayerView playerView, ref Entity entity, ref EntityProperties properties, List<Entity> myEntities)
		{
			EntityType entityType = properties.Build.Value.Options[0];
			int currentUnits = 0;
			foreach (Entity otherEntity in myEntities)
			{
				if (otherEntity.EntityType == entityType)
				{
					currentUnits++;
				}
			}

			EntityProperties buildingEntityProperties = playerView.EntityProperties[entityType];

			if ((currentUnits + 1) * buildingEntityProperties.PopulationUse <= properties.PopulationProvide && _lostResources >= buildingEntityProperties.InitialCost)
			{
				_lostResources -= buildingEntityProperties.InitialCost;

				return new BuildAction(
					entityType,
					new Vec2Int(entity.Position.X + properties.Size, entity.Position.Y + properties.Size - 1)
				);
			}

			return null;
		}

		public void DebugUpdate(PlayerView playerView, DebugInterface debugInterface)
		{
			debugInterface.Send(new DebugCommand.Clear());
			debugInterface.GetState();
		}
	}
}