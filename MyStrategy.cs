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
		private enum CellType : byte
		{
			Free,
			Resource,
			Builder,
			Army,
			Building,
			Enemy
		}

		private readonly CellType[][] _cells = new CellType[80][];

		private readonly EntityType[] _emptyEntityTypes = Array.Empty<EntityType>();
		private readonly EntityType[] _resourceEntityTypes = new EntityType[1] { EntityType.Resource };


		private int _lostResources;

		// Player info
		private Player _selfInfo;
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
		private readonly List<Entity> _enemyTroops = new List<Entity>(10);

		private Entity? otherArmyInMyBase;

		private readonly Dictionary<int, EntityAction> _entityActions = new Dictionary<int, EntityAction>(10);

		private int _countBuilderBasesInPrevTick = 0;
		private int _countRangeBasesInPrevTick = 0;
		private int _countMeleeBasesInPrevTick = 0;
		private int _countHousesInPrevTick = 0;

		private const double enemyRadiusDetection = 6.5d;
		private Vec2Int _myBaseCenter = new Vec2Int(5, 5);

		private int _totalFood = 0;
		private int _consumedFood = 0;

		private Vec2Int _moveTo = new Vec2Int(76, 4);
		private int _angle = 0;

		private const float _buildersPercentage = 0.41f;

		private struct StageProperties
		{
			public StageProperties(int maxTotalFood, int minConsumedFood, int minBuildersCount, int minArmyCount, float buildersPercentage)
			{
				MaxTotalFood = maxTotalFood;
				MinConsumedFood = minConsumedFood;
				MinBuildersCount = minBuildersCount;
				MinArmyCount = minArmyCount;
				BuildersPercentage = buildersPercentage;
			}

			public float BuildersPercentage { get; }

			public int MaxTotalFood { get; }

			public int MinConsumedFood { get; }

			public int MinBuildersCount { get; }

			public int MinArmyCount { get; }
		}

		//private readonly List<StageProperties> _stages = new()
		//{
		//	new StageProperties(15, 0, 0, 0, 1f),
		//	new StageProperties(20, 15, 15, 0, 1f),
		//	new StageProperties(30, 20, 20, 0, 0.5f),
		//	new StageProperties(40, 30, 25, 5, 0.9f),
		//	new StageProperties(45, 40, 34, 6, 0.4f),
		//	new StageProperties(50, 45, 36, 9, 0f),
		//	new StageProperties(60, 50, 36, 14, 0.5f),
		//	new StageProperties(70, 60, 41, 19, 0.4f),
		//	new StageProperties(80, 70, 45, 25, 0f),
		//	new StageProperties(90, 80, 45, 35, 0.5f),
		//	new StageProperties(110, 90, 50, 40, 0.2f),
		//	new StageProperties(130, 100, 50, 50, 0.2f),
		//	new StageProperties(300, 130, 60, 60, 0.2f),
		//};

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

		private bool onlyBuilders = false;
		private bool attacking = false;
		private int turn = 0;

		private EntityAction? _entityRepairAction = null;
		private readonly SortedDictionary<int, EntityTask> entityTasks = new();

		private readonly SortedDictionary<int, BuildersHold> _buildersPositions = new();

		public MyStrategy()
		{
			for (int i = 0; i < 80; i++)
			{
				_cells[i] = new CellType[80];
			}
		}

		private struct BuildersHold
		{
			public Vec2Int PrevPosition;

			public int TickCount;

			public BuildersHold(int tickCount, Vec2Int prevPosition)
			{
				TickCount = tickCount;
				PrevPosition = prevPosition;
			}
		}

		public Action GetAction(PlayerView playerView, DebugInterface debugInterface)
		{
			turn++;
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

			//Debug.WriteLine(PrintMap());

			int buildersCount = builders.Count;

			_consumedFood = buildersCount + rangedUnits.Count + meleeUnits.Count;
			_totalFood = builderBases.Where(q => q.Active).Count() * builderBaseProperties.PopulationProvide
						+ rangedBases.Where(q => q.Active).Count() * rangedBaseProperties.PopulationProvide
						+ meleeBases.Where(q => q.Active).Count() * meleeBaseProperties.PopulationProvide
						+ houses.Where(q => q.Active).Count() * houseProperties.PopulationProvide;

			onlyBuilders = rangedBases.Count == 0 && buildersCount <= 15 && _totalFood <= 20;

			IEnumerable<EntityAction> buildingTasks = entityTasks.Values.Where(q => q.ActionType == ActionType.Build).Select(q => q.EntityAction);
			int lostResources = _lostResources;

			foreach (EntityAction b in buildingTasks)
			{
				EntityProperties entityProperties = GetEntityProperties(b.BuildAction.Value.EntityType);

				lostResources -= entityProperties.InitialCost;
			}

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
				else if (myEntity.EntityType is EntityType.BuilderBase)
				{
					BuilderBaseLogic(ref playerView, ref myEntity, buildersCount, ref buildAction, ref entityProperties, lostResources);
				}
				else if (myEntity.Active && myEntity.EntityType is EntityType.RangedBase)
				{
					EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
					EntityProperties unitEntityProperties = playerView.EntityProperties[buildingEntityType];

					if (!onlyBuilders && ((lostResources - unitEntityProperties.InitialCost) >= unitEntityProperties.InitialCost))
					{
						BuildUnitAction(myEntity.Position, buildingEntityType, entityProperties.Size, unitEntityProperties.InitialCost, ref buildAction, ref lostResources);
					}
				}
				else if (myEntity.Active && myEntity.EntityType is EntityType.MeleeBase && rangedBases.Count == 0)
				{
					EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
					EntityProperties unitEntityProperties = playerView.EntityProperties[buildingEntityType];

					if (!onlyBuilders && ((lostResources - unitEntityProperties.InitialCost) >= unitEntityProperties.InitialCost))
					{
						BuildUnitAction(myEntity.Position, buildingEntityType, entityProperties.Size, unitEntityProperties.InitialCost, ref buildAction, ref lostResources);
					}
				}
				else if (myEntity.EntityType is EntityType.MeleeUnit or EntityType.RangedUnit)
				{
					attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));

					if (FindDistance(myEntity.Position, _moveTo) <= 4.0f)
					{
						switch (_angle)
						{
							case 0:
								_moveTo.X = 4;
								_moveTo.Y = 76;
								break;
							case 1:
								_moveTo.X = 78;
								_moveTo.Y = 78;
								break;
							default:
								if (otherEntities.Count > 0)
								{
									_moveTo = otherEntities[0].Position;
								}
								else
								{
									_moveTo.X = 78;
									_moveTo.Y = 4;
									_angle = -1;
								}
								break;
						}

						attacking = false;
						_angle++;
					}

					if (otherArmyInMyBase.HasValue)
					{
						moveAction = new MoveAction(otherArmyInMyBase.Value.Position, true, true);
						attacking = false;
					}
					else
					{
						if(attacking)
							moveAction = new MoveAction(_moveTo, true, true);

						if (rangedUnits.Count + meleeUnits.Count > 20)
						{
							moveAction = new MoveAction(_moveTo, true, true);
							attacking = true;
						}
						else
							moveAction = new MoveAction(new Vec2Int(20, 20), false, false);
					}
				}
				else if (myEntity.EntityType == EntityType.Turret)
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

			EntityTask[] copy = entityTasks.Values.ToArray();
			foreach (EntityTask t in copy)
			{
				if (t.Processed == false)
				{
					entityTasks.Remove(t.EntityId);
				}
			}

			return result;
		}

		private void BuilderBaseLogic(ref PlayerView playerView, ref Entity myEntity, int buildersCount, ref BuildAction? buildAction, ref EntityProperties entityProperties, int lostResources)
		{
			bool rangedBasesExists = rangedBases.Where(q => q.Active).Any();
			bool needBuilders = (builders.Count < 10 && _lostResources < 200)
									|| (rangedBasesExists && ((float)buildersCount / _totalFood) <= _buildersPercentage)
									|| (!rangedBasesExists && builders.Count < 20);

			EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
			EntityProperties buildingEntityProperties = playerView.EntityProperties[buildingEntityType];

			if (builders.Count < 40 && (onlyBuilders || needBuilders))
			{
				BuildUnitAction(myEntity.Position, buildingEntityType, entityProperties.Size, buildingEntityProperties.InitialCost, ref buildAction, ref lostResources);
			}
		}

		private bool BuilderUnitRepairTaskLogic(ref EntityTask task, ref EntityAction eAction, ref EntityAction? entityAction, int myEntityId, Vec2Int builderPosition)
		{
			RepairAction rAction = eAction.RepairAction.Value;

			if (TryGetBuilding(rAction.Target, out Entity repairTarget))
			{
				EntityProperties repairTargetProperties = GetEntityProperties(repairTarget.EntityType);
				if (!CheckBuilderOnBuildingEdge(repairTarget.Position, builderPosition, repairTargetProperties.Size)
					&& TryGetFreeUnitLocation(repairTarget.Position, repairTargetProperties.Size, out builderPosition))
				{
					entityAction = new EntityAction(new MoveAction(builderPosition, true, false), null, null, eAction.RepairAction);
					task.Processed = true;
					entityTasks[myEntityId] = new EntityTask(myEntityId, true, entityAction.Value, ActionType.Repair);
					return true;
				}

				if (repairTarget.EntityType == EntityType.House)
				{
					if (repairTarget.Health == repairTargetProperties.MaxHealth)
					{
						entityTasks.Remove(myEntityId);
					}
					else
					{
						task.Processed = true;
						entityAction = eAction;
					}
				}
				else if (repairTarget.EntityType == EntityType.RangedBase)
				{
					if (repairTarget.Health == repairTargetProperties.MaxHealth)
					{
						entityTasks.Remove(myEntityId);
						_entityRepairAction = null;
					}
					else
					{
						_entityRepairAction = eAction;
						entityAction = eAction;
						task.Processed = true;
					}
				}

				return true;
			}
			else
			{
				entityTasks.Remove(myEntityId);
			}

			return false;
		}

		private bool BuilderUnitBuildingTaskLogic(ref EntityTask task, ref EntityAction eAction, ref EntityAction? entityAction, int myEntityId, Vec2Int builderPosition)
		{
			if (_buildersPositions.TryGetValue(myEntityId, out BuildersHold prevPos)
				&& (prevPos.PrevPosition.X == builderPosition.X
					&& prevPos.PrevPosition.Y == builderPosition.Y))
			{
				if (prevPos.TickCount > 3)
				{
					entityTasks.Remove(myEntityId);

					entityAction = new EntityAction(null, null, new AttackAction(null, new AutoAttack(60, _resourceEntityTypes)), null);
					return true;
				}

				_buildersPositions[myEntityId] = new BuildersHold(prevPos.TickCount + 1, builderPosition);
			}

			_buildersPositions[myEntityId] = new BuildersHold(1, builderPosition);

			BuildAction bAction = eAction.BuildAction.Value;
			EntityType neededBuildType = bAction.EntityType;
			EntityProperties bProperties = GetEntityProperties(neededBuildType);

			int neededBuildingsCountInPrevTick = neededBuildType switch
			{
				EntityType.BuilderBase => _countBuilderBasesInPrevTick,
				EntityType.RangedBase => _countRangeBasesInPrevTick,
				EntityType.MeleeBase => _countMeleeBasesInPrevTick,
				EntityType.House => _countHousesInPrevTick
			};

			IEnumerable<Entity> entities = (bAction.EntityType == EntityType.House ? houses : rangedBases).Where(q => q.Active == false);

			if (entities.Any())
			{
				EntityProperties buildingProperties = bAction.EntityType == EntityType.House ? houseProperties : rangedBaseProperties;

				IEnumerable<Entity> inactiveBuildings = entities.Where(q => q.Position.X == bAction.Position.X && q.Position.Y == bAction.Position.Y && bAction.EntityType == q.EntityType);

				if (inactiveBuildings.Any())
				{
					entityTasks.Remove(myEntityId);

					Entity lastBuilding = inactiveBuildings.First();
					if (lastBuilding.Health < buildingProperties.MaxHealth)
					{
						entityAction = new EntityAction(new MoveAction(new Vec2Int(lastBuilding.Position.X + 1, lastBuilding.Position.Y + 1), true, false), null, null, new RepairAction(lastBuilding.Id));
						_buildersPositions.Remove(myEntityId);
						entityTasks.Add(myEntityId, new EntityTask(myEntityId, true, entityAction.Value, ActionType.Repair));
						return true;
					}
				}
			}

			Vec2Int buildingPosition = bAction.Position;
			Vec2Int moveToBuilderPosition = eAction.MoveAction.Value.Target;
			bool ignoreBuildingBuffer = bAction.EntityType == EntityType.RangedBase;

			if (CheckFreeLocation(buildingPosition, bProperties.Size, ignoreBuildingBuffer))
			{
				if (CheckBuilderOnBuildingEdge(buildingPosition, builderPosition, bProperties.Size))
				{
					moveToBuilderPosition = builderPosition;
					entityAction = new EntityAction(new MoveAction(builderPosition, true, true), bAction, null, null);
					entityTasks[myEntityId] = new EntityTask(myEntityId, false, entityAction.Value, ActionType.Build);
				}
				else
				{
					entityAction = eAction;
					task.Processed = true;
				}
			}
			else if (TryGetFreeLocation(bProperties.Size, out buildingPosition, out moveToBuilderPosition, ignoreBuildingBuffer))
			{
				entityAction = new EntityAction(new MoveAction(moveToBuilderPosition, true, false), new BuildAction(bAction.EntityType, buildingPosition), null, null);
				entityTasks[myEntityId] = new EntityTask(myEntityId, false, entityAction.Value, ActionType.Build);
			}
			else
			{
				entityTasks.Remove(myEntityId);
				return false;
			}

			LockBuildingPosition(buildingPosition, bProperties.Size);

			return true;
		}
		
		private void BuilderUnitLogic(ref MoveAction? moveAction, ref BuildAction? buildAction, ref RepairAction? repairAction, ref AttackAction? attackAction, ref EntityAction? entityAction, ref EntityType[] validAutoAttackTargets, in EntityProperties entityProperties, in Entity myEntity)
		{
			Vec2Int builderPosition = myEntity.Position;
			IEnumerable<Entity> threatsWorker = _enemyTroops.Where(q => Math.Abs(FindDistance(q.Position, builderPosition)) < enemyRadiusDetection);
			if (threatsWorker.Any())
			{
				moveAction = new MoveAction(_myBaseCenter, true, false);
				//_threatOfBuilders.Add(threatsWorker.First());
				return;
			}

			if (entityTasks.TryGetValue(myEntity.Id, out EntityTask task))
			{
				EntityAction eAction = task.EntityAction;
				if (task.ActionType == ActionType.Repair && BuilderUnitRepairTaskLogic(ref task, ref eAction, ref entityAction, myEntity.Id, myEntity.Position))
				{
					return;
				}
				else if (task.ActionType == ActionType.Build && BuilderUnitBuildingTaskLogic(ref task, ref eAction, ref entityAction, myEntity.Id, myEntity.Position))
				{
					return;
				}
			}
			else if (_entityRepairAction.HasValue)
			{
				RepairAction rAction = _entityRepairAction.Value.RepairAction.Value;

				if (TryGetBuilding(rAction.Target, out Entity repairTarget)
					&& repairTarget.EntityType == EntityType.RangedBase
					&& entityTasks.Count(q => q.Value.EntityAction.RepairAction.HasValue && q.Value.EntityAction.RepairAction.Value.Target == rAction.Target) < 4)
				{
					moveAction = new MoveAction(new Vec2Int(repairTarget.Position.X + 1, repairTarget.Position.Y + 1), true, false);

					if (repairTarget.Health == rangedBaseProperties.MaxHealth)
					{
						entityTasks.Remove(myEntity.Id);
						_entityRepairAction = null;
					}
					else
					{
						entityAction = new EntityAction(moveAction, buildAction, attackAction, rAction);

						entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Repair));
						return;
					}
				}
			}

			IEnumerable<BuildAction> buildingTasks = entityTasks.Where(q => q.Value.ActionType == ActionType.Build).Select(q => q.Value.EntityAction.BuildAction.Value);
			int rangedBasebuildingTasksCount = buildingTasks.Where(q => q.EntityType == EntityType.RangedBase).Count();
			int housebuildingTasksCount = buildingTasks.Where(q => q.EntityType == EntityType.House).Count();

			IEnumerable<RepairAction> repairingTasks = entityTasks.Where(q => q.Value.ActionType == ActionType.Repair).Select(q => q.Value.EntityAction.RepairAction.Value);

			int rangedBaseRepairingTasksCount = 0;
			int houseRepairingTasksCount = 0;

			foreach (RepairAction r in repairingTasks)
			{
				if (TryGetBuilding(r.Target, out Entity rapairTarget))
				{
					if (rapairTarget.EntityType == EntityType.House)
					{
						houseRepairingTasksCount++;
					}
					else if (rapairTarget.EntityType == EntityType.RangedBase)
					{
						rangedBaseRepairingTasksCount++;
					}
				}
			}

			if (rangedBases.Count == 0
				&& rangedBasebuildingTasksCount == 0
				&& rangedBaseRepairingTasksCount == 0
				&& _lostResources >= rangedBaseProperties.InitialCost
				&& TryGetFreeLocation(rangedBaseProperties.Size, out Vec2Int buildingPositon, out Vec2Int moveToBuilderPosition, true))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(moveToBuilderPosition, true, false);
				buildAction = new BuildAction(EntityType.RangedBase, buildingPositon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);

				LockBuildingPosition(buildingPositon, rangedBaseProperties.Size);

				entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Build));
				return;
			}

			int foodDeference = _totalFood - _consumedFood;
			bool needNewHouse = foodDeference < 10
									&& (housebuildingTasksCount + houseRepairingTasksCount) < 2;

			bool needMoreHouse = _totalFood <= 60 && foodDeference < 15 && _lostResources > 300 && (housebuildingTasksCount + houseRepairingTasksCount) < 3;

			if ((needNewHouse || needMoreHouse) && _lostResources >= houseProperties.InitialCost && TryGetFreeLocation(houseProperties.Size, out Vec2Int positon, out moveToBuilderPosition))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(moveToBuilderPosition, true, false);
				buildAction = new BuildAction(EntityType.House, positon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
				LockBuildingPosition(in positon, rangedBaseProperties.Size);
				entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Build));
				return;
			}

			validAutoAttackTargets = _resourceEntityTypes;
			attackAction = new AttackAction(null, new AutoAttack(60, validAutoAttackTargets));
		}

		private void LockBuildingPosition(in Vec2Int buildingPosition, int size)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					_cells[yi][xi] = CellType.Building;
				}
			}
		}

		private double FindDistance(Vec2Int position1, Vec2Int position2)
		{
			double xDif = position1.X - position2.X;
			double yDif = position1.Y - position2.Y;
			return Math.Sqrt((xDif * xDif) + (yDif * yDif));
		}

		private bool CheckBuilderOnBuildingEdge(Vec2Int buildingPosition, Vec2Int builderPosition, int buildingSize)
		{
			int x1 = buildingPosition.X - 1;
			int x2 = buildingPosition.X + buildingSize;
			int y1 = buildingPosition.Y - 1;
			int y2 = buildingPosition.Y + buildingSize;

			if ((builderPosition.X > x1 && builderPosition.X < x2 && (builderPosition.Y == y1 || builderPosition.Y == y2))
				|| (builderPosition.Y > y1 && builderPosition.Y < y2 && (builderPosition.X == x1 || builderPosition.X == x2)))
			{
				return true;
			}

			return false;
		}

		private bool CheckFreeLocation(Vec2Int buildingPosition, int size, bool ignoreBuffer = false)
		{
			return CheckFreeLocation(buildingPosition.X, buildingPosition.Y, size, ignoreBuffer);
		}

		private bool CheckFreeLocation(int x, int y, int size, bool ignoreBuffer)
		{
			bool isBusy = false;

			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					if (_cells[yi][xi] != CellType.Free)
					{
						isBusy = true;
						break;
					}
				}

				if (isBusy)
				{
					break;
				}
			}

			if (isBusy)
			{
				return false;
			}

			int x1 = x - 1;
			int x2 = x + size;
			int y1 = y - 1;
			int y2 = y + size;

			if (x < 2 || y < 2 || ignoreBuffer)
			{
				return true;
			}

			for (int i = 0; i < size; i++)
			{
				int xi = x + i;
				int yi = y + i;

				if (!(_cells[y2][xi] is CellType.Free or CellType.Builder or CellType.Army)
					|| !(_cells[y1][xi] is CellType.Free or CellType.Builder or CellType.Army)
					|| !(_cells[yi][x2] is CellType.Free or CellType.Builder or CellType.Army)
					|| !(_cells[yi][x1] is CellType.Free or CellType.Builder or CellType.Army))
				{
					return false;
				}
			}

			return true;
		}

		private bool TryGetFreeLocation(int size, out Vec2Int buildingPosition, out Vec2Int builderPosition, bool ignoreBuffer = false)
		{
			for (int num = 1; num < 40; num++)
			{
				for (int x = 0; x < num; x++)
				{
					for (int y = 0; y < num; y++)
					{
						if (CheckFreeLocation(x, y, size, ignoreBuffer))
						{
							buildingPosition = new Vec2Int(x, y);
							if (TryGetFreeUnitLocation(buildingPosition, size, out builderPosition))
							{
								return true;
							}
						}
					}
				}
			}

			builderPosition = default;
			buildingPosition = default;
			return false;
		}

		private bool TryGetFreeUnitLocation(Vec2Int buildingPosition, int size, out Vec2Int unitPosition)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			int x1 = x - 1;
			int x2 = x + size;
			int y1 = y - 1;
			int y2 = y + size;

			for (int i = size - 1; i > 0; i--)
			{
				int xi = x + i;
				int yi = y + i;

				if (_cells[yi][x2] is CellType.Free)
				{
					unitPosition = new Vec2Int(x2, yi);
					return true;
				}
				else if (_cells[y2][xi] is CellType.Free)
				{
					unitPosition = new Vec2Int(xi, y2);
					return true;
				}
				else if (y1 >= 0 && _cells[y1][xi] is CellType.Free)
				{
					unitPosition = new Vec2Int(xi, y1);
					return true;
				}
				else if (x1 >= 0 && _cells[yi][x1] is CellType.Free)
				{
					unitPosition = new Vec2Int(x1, yi);
					return true;
				}
			}

			unitPosition = default;
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
			void FillCells(int x, int y, int size, CellType cellType)
			{
				for (int xi = x; xi < x + size; xi++)
				{
					for (int yi = y; yi < y + size; yi++)
					{
						_cells[yi][xi] = cellType;
					}
				}
			}

			int entitiesLenght = playerView.Entities.Length;

			otherArmyInMyBase = null;

			for (int i = 0; i < entitiesLenght; i++)
			{
				Entity entity = playerView.Entities[i];

				int x = entity.Position.X;
				int y = entity.Position.Y;

				if (entity.PlayerId == _selfInfo.Id)
				{
					myEntities.Add(entity);
					switch (entity.EntityType)
					{
						case EntityType.BuilderBase:
							builderBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, builderBaseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderUnit:
							builders.Add(entity);
							FillCells(x, y, builderUnitProperties.Size, CellType.Builder);
							break;
						case EntityType.RangedBase:
							rangedBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, rangedBaseProperties.Size, CellType.Building);
							break;
						case EntityType.RangedUnit:
							rangedUnits.Add(entity);
							FillCells(x, y, rangedUnitProperties.Size, CellType.Army);
							break;
						case EntityType.MeleeBase:
							meleeBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, meleeBaseProperties.Size, CellType.Building);
							break;
						case EntityType.MeleeUnit:
							meleeUnits.Add(entity);
							FillCells(x, y, meleeUnitProperties.Size, CellType.Army);
							break;
						case EntityType.House:
							houses.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, houseProperties.Size, CellType.Building);
							break;
						case EntityType.Turret:
							//turrets.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
					}
				}
				else if (entity.EntityType == EntityType.Resource)
				{
					resources.Add(entity);
					_cells[y][x] = CellType.Resource;
				}
				else
				{
					otherEntities.Add(entity);

					switch (entity.EntityType)
					{
						case EntityType.Wall:
							FillCells(x, y, wallProperties.Size, CellType.Building);
							break;
						case EntityType.House:
							FillCells(x, y, houseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderBase:
							FillCells(x, y, builderBaseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderUnit:
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.MeleeBase:
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
						case EntityType.MeleeUnit:
							_enemyTroops.Add(entity);
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.RangedBase:
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
						case EntityType.RangedUnit:
							_enemyTroops.Add(entity);
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.Turret:
							_enemyTroops.Add(entity);
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
					}

					if (entity.Position.X <= 32 && entity.Position.Y <= 32)
					{
						if (otherArmyInMyBase.HasValue)
						{
							if (FindDistance(entity.Position) < FindDistance(otherArmyInMyBase.Value.Position))
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
			return FindDistance(entityPosition, _myBaseCenter);
		}

		private void ClearLists()
		{
			myEntities.Clear();
			resources.Clear();
			otherEntities.Clear();
			_enemyTroops.Clear();

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
					_cells[i][k] = CellType.Free;
				}
			}
		}

		private bool TryGetBuilding(int entityId, out Entity building)
		{
			building = allBuildings.FirstOrDefault(e => e.Id == entityId);

			return building.Id > 0;
		}

		private void BuildUnitAction(Vec2Int buildingPosition, EntityType buildingEntityType, int size, int initialCost, ref BuildAction? buildAction, ref int lostResources)
		{
			if (_consumedFood + 1 <= _totalFood && lostResources >= initialCost && TryGetFreeUnitLocation(buildingPosition, size, out Vec2Int unitPosition))
			{
				lostResources -= initialCost;

				buildAction = new BuildAction(buildingEntityType, unitPosition);
			}
		}

		public void DebugUpdate(PlayerView playerView, DebugInterface debugInterface)
		{
			debugInterface.Send(new DebugCommand.Clear());
			debugInterface.GetState();
		}

		private string PrintMap()
		{
			StringBuilder sb = new StringBuilder(80 * 80 + 80);
			for (int x = 0; x < 80; x++)
			{
				for (int y = 0; y < 80; y++)
				{
					sb.Append(_cells[x][79 - y] == CellType.Free ? 1 : 0);
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}
	}

	public struct EntityTask
	{
		public int EntityId;

		public bool BuildingIsBuild;

		public bool Processed;

		public EntityAction EntityAction;

		public ActionType ActionType;

		public EntityTask(int entityId, bool buildingIsBuild, EntityAction entityAction, ActionType actionType)
		{
			EntityId = entityId;
			BuildingIsBuild = buildingIsBuild;
			Processed = true;
			EntityAction = entityAction;
			ActionType = actionType;
		}
	}

	public enum ActionType : byte
	{
		Build,
		Repair
	}
}