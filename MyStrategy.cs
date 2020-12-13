using Aicup2020.Model;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

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
		private readonly List<Entity> allBuildings = new List<Entity>(3);

		private readonly List<Entity> resources = new List<Entity>(1000);
		private readonly List<Entity> otherEntities = new List<Entity>(100);

		private Entity? otherArmyInMyBase;

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

		private const float _buildersPercentage = 0.41f;
		private const float _armyPercentage = 1.0f - _buildersPercentage;

		private EntityProperties houseProperties;
		private EntityProperties builderBaseProperties;
		private EntityProperties rangedBaseProperties;
		private EntityProperties meleeBaseProperties;
		private EntityProperties builderUnitProperties;
		private EntityProperties rangedUnitProperties;
		private EntityProperties meleeUnitProperties;
		private EntityProperties wallProperties;
		private EntityProperties turretProperties;
		private EntityProperties resourceProperties;

		private bool needHouse = false;
		private bool newHouseBuilding = false;
		private bool newMeleeBaseBuilding = false;

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

			houseProperties = playerView.EntityProperties[EntityType.House];
			builderBaseProperties = playerView.EntityProperties[EntityType.BuilderBase];
			rangedBaseProperties = playerView.EntityProperties[EntityType.RangedBase];
			meleeBaseProperties = playerView.EntityProperties[EntityType.MeleeBase];
			wallProperties = playerView.EntityProperties[EntityType.Wall];
			builderUnitProperties = playerView.EntityProperties[EntityType.BuilderUnit];
			rangedUnitProperties = playerView.EntityProperties[EntityType.RangedUnit];
			meleeUnitProperties = playerView.EntityProperties[EntityType.MeleeUnit];
			turretProperties = playerView.EntityProperties[EntityType.Turret];
			resourceProperties = playerView.EntityProperties[EntityType.Resource];

			ClearLists();

			CollectEntities(playerView);

			Debug.WriteLine(PrintMap());

			if(_moveTo.X == 0 && _moveTo.Y == 0)
			{
				_moveTo.Y = _moveTo.X = playerView.MapSize - 1;
			}

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

				EntityProperties entityProperties = GetEntityProperties(myEntity.EntityType);

				if (myEntity.EntityType == EntityType.BuilderUnit)
				{
					BuilderUnitLogic(ref moveAction, ref buildAction, ref repairAction, ref attackAction, ref entityAction, ref validAutoAttackTargets, in entityProperties, in myEntity);
				}
				else if(myEntity.EntityType is EntityType.BuilderBase)
				{
					BuilderBaseLogic(ref playerView, ref myEntity, buildersCount, ref buildAction, ref entityProperties);
				}
				else if (myEntity.EntityType is EntityType.RangedBase or EntityType.MeleeBase)
				{
					EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
					bool needArmy = ((float)(myEntity.EntityType == EntityType.RangedBase ? rangedUnits.Count : meleeUnits.Count) / _totalFood) <= _armyPercentage;
					EntityProperties buildingEntityProperties = playerView.EntityProperties[buildingEntityType];

					if((!needHouse || (needHouse && _lostResources - buildingEntityProperties.InitialCost >= houseProperties.InitialCost)) && needArmy)
					{
						buildAction = BuildAction(myEntity.Position, buildingEntityType, entityProperties.Size, buildingEntityProperties.InitialCost, myEntities);
					}
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

					if(otherArmyInMyBase.HasValue)
						moveAction = new MoveAction(otherArmyInMyBase.Value.Position, true, true);
					else
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

		private void BuilderBaseLogic(ref PlayerView playerView, ref Entity myEntity, int buildersCount, ref BuildAction? buildAction, ref EntityProperties entityProperties)
		{
			bool needBuilders = ((float)buildersCount / _totalFood) < _buildersPercentage;
			EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
			EntityProperties buildingEntityProperties = playerView.EntityProperties[buildingEntityType];

			if ((!needHouse || (needHouse && _lostResources - buildingEntityProperties.InitialCost >= houseProperties.InitialCost)) && needBuilders)
			{
				buildAction = BuildAction(myEntity.Position, buildingEntityType, entityProperties.Size, buildingEntityProperties.InitialCost, myEntities);
			}
		}

		private void BuilderUnitLogic(ref MoveAction? moveAction, ref BuildAction? buildAction, ref RepairAction? repairAction, ref AttackAction? attackAction, ref EntityAction? entityAction, ref EntityType[] validAutoAttackTargets, in EntityProperties entityProperties, in Entity myEntity)
		{
			if (_repairsActions.TryGetValue(myEntity.Id, out EntityAction entityRepairAction))
			{
				RepairAction rAction = entityRepairAction.RepairAction.Value;

				if (TryGetBuilding(rAction.Target, out Entity repairTarget))
				{
					if(repairTarget.EntityType == EntityType.House)
					{
						if (repairTarget.Health == houseProperties.MaxHealth)
						{
							_repairsActions.Remove(myEntity.Id);
						}
						else
						{
							entityAction = entityRepairAction;
							return;
						}
					}
					else if(repairTarget.EntityType == EntityType.MeleeBase)
					{
						if (repairTarget.Health == meleeBaseProperties.MaxHealth)
						{
							_repairsActions.Remove(myEntity.Id);
						}
						else
						{
							entityAction = entityRepairAction;
							return;
						}
					}
				}
			}

			if (_buildersActions.TryGetValue(myEntity.Id, out EntityAction entityBuildAction))
			{
				BuildAction bAction = entityBuildAction.BuildAction.Value;
				EntityType neededBuildType = bAction.EntityType;

				int neededBuildingsCountInPrevTick = neededBuildType switch
				{
					EntityType.BuilderBase => _countBuilderBasesInPrevTick,
					EntityType.RangedBase => _countRangeBasesInPrevTick,
					EntityType.MeleeBase => _countMeleeBasesInPrevTick,
					EntityType.House => _countHousesInPrevTick
				};

				if (bAction.EntityType == EntityType.House)
				{
					if (houses.Count > 0 && houses.Count > neededBuildingsCountInPrevTick)
					{
						_buildersActions.Remove(myEntity.Id);

						Entity lastHouse = houses.OrderByDescending(q => q.Id).First();
						if (lastHouse.Health < houseProperties.MaxHealth)
						{
							newHouseBuilding = false;
							repairAction = new RepairAction(lastHouse.Id);
							entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
							_repairsActions.Add(myEntity.Id, entityAction.Value);
							return;
						}
					}
				}

				if(bAction.EntityType == EntityType.MeleeBase)
				{
					if (meleeBases.Count > 0 && meleeBases.Count > neededBuildingsCountInPrevTick)
					{
						_buildersActions.Remove(myEntity.Id);

						Entity lastMeleeBase = meleeBases.OrderByDescending(q => q.Id).First();
						if (lastMeleeBase.Health < meleeBaseProperties.MaxHealth)
						{
							newMeleeBaseBuilding = false;
							repairAction = new RepairAction(lastMeleeBase.Id);
							entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
							_repairsActions.Add(myEntity.Id, entityAction.Value);
							return;
						}
					}
				}

				entityAction = entityBuildAction;
				return;
			}

			if (needHouse && !newHouseBuilding && _lostResources >= houseProperties.InitialCost && TryGetFreeLocation(houseProperties.Size, out Vec2Int positon, out Vec2Int houseBuilderPosition))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(houseBuilderPosition, true, true);
				buildAction = new BuildAction(EntityType.House, positon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
				_buildersActions.Add(myEntity.Id, entityAction.Value);
				newHouseBuilding = true;
				return;
			}


			if(_lostResources > 150 && !newMeleeBaseBuilding && _lostResources >= meleeBaseProperties.InitialCost && TryGetFreeLocation(meleeBaseProperties.Size, out Vec2Int meleeBasePositon, out Vec2Int meleeBaseBuilderPosition))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(meleeBaseBuilderPosition, true, true);
				buildAction = new BuildAction(EntityType.MeleeBase, meleeBasePositon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
				_buildersActions.Add(myEntity.Id, entityAction.Value);
				newMeleeBaseBuilding = true;
				return;
			}

			validAutoAttackTargets = _resourceEntityTypes;
			attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange * 5, validAutoAttackTargets));
		}

		private bool TryGetFreeLocation(int size, out Vec2Int buildingPosition, out Vec2Int builderPosition)
		{
			builderPosition = buildingPosition = new Vec2Int(0, 0);

			for (int num = 1; num < 40; num++)
			{
				for (int x = 1; x < num; x++)
				{
					bool notFree = false;
					int y = num;
					for (int xi = x; xi <= x + size; xi++)
					{
						for (int yi = y; yi <= y + size; yi++)
						{
							if (_cells[yi][xi])
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
						bool builderPositionFound = false;

						for (int i = 0; i < size; i++)
						{
							if (!_cells[y - 1][x + i])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y - 1);
								break;
							}
							else if (!_cells[y + size + 1][x + i])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
								break;
							}
							else if (!_cells[y + i][x - 1])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
							}
							else if (!_cells[y + i][x + size])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
							}
						}

						if (builderPositionFound)
						{
							buildingPosition = new Vec2Int(x, y);
							return true;
						}
					}
				}
				
				for (int y = 1; y < num; y++)
				{
					bool notFree = false;
					int x = num;
					for (int xi = x; xi <= x + size; xi++)
					{
						for (int yi = y; yi <= y + size; yi++)
						{
							if (_cells[yi][xi])
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
						bool builderPositionFound = false;

						for (int i = 0; i < size; i++)
						{
							if (!_cells[y - 1][x + i])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y - 1);
								break;
							}
							else if (!_cells[y + size + 1][x + i])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
								break;
							}
							else if (!_cells[y + i][x - 1])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
							}
							else if (!_cells[y + i][x + size])
							{
								builderPositionFound = true;
								builderPosition = new Vec2Int(x + i, y + size);
							}
						}

						if (builderPositionFound)
						{
							buildingPosition = new Vec2Int(x, y);
							return true;
						}
					}
				}
			}

			return false;
		}

		private EntityProperties GetEntityProperties(EntityType entityType)
		{
			return entityType switch
			{
				EntityType.Wall => wallProperties,
				EntityType.House => houseProperties,
				EntityType.BuilderBase => builderBaseProperties,
				EntityType.BuilderUnit => builderUnitProperties,
				EntityType.MeleeBase => meleeBaseProperties,
				EntityType.MeleeUnit => meleeUnitProperties,
				EntityType.RangedBase => rangedBaseProperties,
				EntityType.RangedUnit => rangedUnitProperties,
				EntityType.Resource => resourceProperties,
				EntityType.Turret => turretProperties,
				_ => default,
			};
		}

		private void CollectEntities(PlayerView playerView)
		{
			int entitiesLenght = playerView.Entities.Length;

			otherArmyInMyBase = null;

			for (int i = 0; i < entitiesLenght; i++)
			{
				Entity entity = playerView.Entities[i];
				EntityProperties entityProperties = GetEntityProperties(entity.EntityType);

				int x = entity.Position.X;
				int y = entity.Position.Y;
				//_cells[y][x] = true;

				for(int xi = x; xi< x + entityProperties.Size; xi++)
				{
					for (int yi = y; yi < y + entityProperties.Size; yi++)
						_cells[yi][xi] = true;
				}

				if (entity.PlayerId == _selfInfo.Id)
				{
					myEntities.Add(entity);
					switch (entity.EntityType)
					{
						case EntityType.BuilderBase:
							builderBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.BuilderUnit:
							builders.Add(entity);
							break;
						case EntityType.RangedBase:
							rangedBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.RangedUnit:
							rangedUnits.Add(entity);
							break;
						case EntityType.MeleeBase:
							meleeBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.MeleeUnit:
							meleeUnits.Add(entity);
							break;
						case EntityType.House:
							houses.Add(entity);
							allBuildings.Add(entity);
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

					if(entity.Position.X <= 32 && entity.Position.Y <= 32)
					{
						if(otherArmyInMyBase.HasValue)
						{
							if(FindDistance(entity.Position) < FindDistance(otherArmyInMyBase.Value.Position))
							{
								otherArmyInMyBase = entity;
							}
						}
						else
						{
							otherArmyInMyBase = entity;
						}
					}
				}
			}
		}

		private double FindDistance(Vec2Int entityPosition)
		{
			return Math.Sqrt(entityPosition.X - 5 + entityPosition.Y - 5);
		}

		private void ClearLists()
		{
			myEntities.Clear();
			resources.Clear();
			otherEntities.Clear();

			builders.Clear();
			rangedUnits.Clear();
			meleeUnits.Clear();

			allBuildings.Clear();
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

		private bool TryGetBuilding(int entityId, out Entity building)
		{
			building = allBuildings.FirstOrDefault(e => e.Id == entityId);

			return building.Id > 0;
		}

		private BuildAction? BuildAction(Vec2Int buildingPosition, EntityType buildingEntityType, int size, int initialCost, List<Entity> myEntities)
		{
			int currentUnits = 0;
			foreach (Entity otherEntity in myEntities)
			{
				if (otherEntity.EntityType == buildingEntityType)
				{
					currentUnits++;
				}
			}

			if (_consumedFood + 1 <= _totalFood && _lostResources >= initialCost)
			{
				_lostResources -= initialCost;

				return new BuildAction(
					buildingEntityType,
					new Vec2Int(buildingPosition.X + size, buildingPosition.Y + size - 1)
				);
			}

			return null;
		}

		public void DebugUpdate(PlayerView playerView, DebugInterface debugInterface)
		{
			debugInterface.Send(new DebugCommand.Clear());
			debugInterface.GetState();
		}

		private string PrintMap()
		{
			var sb = new StringBuilder(80*80+80);
			for(int x = 0; x < 80; x++)
			{
				for(int y = 0; y < 80; y++)
				{
					sb.Append(_cells[x][79 - y] ? 1 : 0);
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}
	}
}