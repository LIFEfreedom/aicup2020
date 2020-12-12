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

		private int _countBuilderBasesInPrevTick = 0;
		private int _countRangeBasesInPrevTick = 0;
		private int _countMeleeBasesInPrevTick = 0;
		private int _countHousesInPrevTick = 0;

		private int _totalFood = 0;
		private int _consumedFood = 0;

		private Vec2Int _moveTo = new Vec2Int(0,0);
		private int _angle = 0;

		private const float _buildersPercentage = 0.35f;
		private const float _armyPercentage = 1.0f - _buildersPercentage;

		private EntityProperties houseProperties;
		private EntityProperties builderBaseProperties;
		private EntityProperties rangedBaseProperties;
		private EntityProperties meleeBaseProperties;

		private bool needHouse = false;
		private bool newHouseBuilding = false;

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

			if(_moveTo.X == 0 && _moveTo.Y == 0)
			{
				_moveTo.Y = _moveTo.X = playerView.MapSize - 1;
			}

			houseProperties = playerView.EntityProperties[EntityType.House];
			builderBaseProperties = playerView.EntityProperties[EntityType.BuilderBase];
			rangedBaseProperties = playerView.EntityProperties[EntityType.RangedBase];
			meleeBaseProperties = playerView.EntityProperties[EntityType.MeleeBase];

			// TODO: посчитать количество потребляемой еды используя PopulationProvide

			int buildersCount = builders.Count;

			_consumedFood = buildersCount + rangedUnits.Count + meleeUnits.Count;
			_totalFood = builderBases.Count * builderBaseProperties.PopulationProvide
						+ rangedBases.Count * rangedBaseProperties.PopulationProvide
						+ meleeBases.Count * meleeBaseProperties.PopulationProvide
						+ houses.Count * houseProperties.PopulationProvide;

			needHouse = _totalFood - _consumedFood < 5;

			//newHouseBuilding = false;

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
					BuilderUnitLogic(ref moveAction, ref buildAction, ref repairAction, ref attackAction, ref entityAction, ref validAutoAttackTargets, in entityProperties, in myEntity);
				}
				else if(!needHouse && myEntity.EntityType is EntityType.BuilderBase)
				{
					if (((float)buildersCount / _totalFood) < _buildersPercentage)
					{
						buildAction = BuildAction(ref playerView, ref myEntity, ref entityProperties, myEntities);
					}
				}
				else if (!needHouse && myEntity.EntityType is EntityType.RangedBase or EntityType.MeleeBase)
				{
					buildAction = BuildAction(ref playerView, ref myEntity, ref entityProperties, myEntities);
				}
				else if (myEntity.EntityType is EntityType.MeleeUnit or EntityType.RangedUnit)
				{
					attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));

					if (_moveTo.X == myEntity.Position.X && _moveTo.Y == myEntity.Position.Y)
					{
						switch(_angle)
						{
							case 0:
								_moveTo.Y = 1;
								_moveTo.X = playerView.MapSize - 1;
								break;
							case 1:
								_moveTo.X = 1;
								_moveTo.Y = playerView.MapSize - 1;
								break;
							default:
								_moveTo.Y = 10;
								_moveTo.X = 10;
								break;
						}

						_angle++;
					}

					moveAction = new MoveAction(_moveTo, true, true);
				}
				else if(myEntity.EntityType == EntityType.Turret)
				{
					attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));
				}

				result.EntityActions[myEntity.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			_countBuilderBasesInPrevTick = builderBases.Count;
			_countRangeBasesInPrevTick = rangedBases.Count;
			_countMeleeBasesInPrevTick = meleeBases.Count;
			_countHousesInPrevTick = houses.Count;

			return result;
		}

		private void BuilderUnitLogic(ref MoveAction? moveAction, ref BuildAction? buildAction, ref RepairAction? repairAction, ref AttackAction? attackAction, ref EntityAction? entityAction, ref EntityType[] validAutoAttackTargets, in EntityProperties entityProperties, in Entity myEntity)
		{
			if (_repairsActions.TryGetValue(myEntity.Id, out EntityAction entityRepairAction))
			{
				if (!houses.Any() || houses.First(q => q.Id == entityRepairAction.RepairAction.Value.Target).Health == houseProperties.MaxHealth)
				{
					_repairsActions.Remove(myEntity.Id);
				}
				else
				{
					entityAction = entityRepairAction;
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
							newHouseBuilding = false;
							repairAction = new RepairAction(lastHouse.Id);
							entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
							_repairsActions.Add(myEntity.Id, entityAction.Value);
						}
					}
					else
					{
						entityAction = entityBuildAction;
						return;
					}
				}
				else
				{
					entityAction = entityBuildAction;
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
				attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange * 5, validAutoAttackTargets));
			}
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

			if (_consumedFood + 1 <= _totalFood && _lostResources >= buildingEntityProperties.InitialCost)
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