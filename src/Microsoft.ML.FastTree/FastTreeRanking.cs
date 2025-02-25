﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.EntryPoints;
using Microsoft.ML.Internal.Utilities;
using Microsoft.ML.Model;
using Microsoft.ML.Runtime;
using Microsoft.ML.Trainers.FastTree;

// REVIEW: Do we really need all these names?
[assembly: LoadableClass(FastTreeRankingTrainer.Summary, typeof(FastTreeRankingTrainer), typeof(FastTreeRankingTrainer.Options),
    new[] { typeof(SignatureRankerTrainer), typeof(SignatureTrainer), typeof(SignatureTreeEnsembleTrainer), typeof(SignatureFeatureScorerTrainer) },
    FastTreeRankingTrainer.UserNameValue,
    FastTreeRankingTrainer.LoadNameValue,
    FastTreeRankingTrainer.ShortName,

    // FastRank names
    "FastRankRanking",
    "FastRankRankingWrapper",
    "rank",
    "frrank",
    "btrank")]

[assembly: LoadableClass(typeof(FastTreeRankingModelParameters), null, typeof(SignatureLoadModel),
    "FastTree Ranking Executor",
    FastTreeRankingModelParameters.LoaderSignature)]

[assembly: LoadableClass(typeof(void), typeof(FastTree), null, typeof(SignatureEntryPointModule), "FastTree")]

namespace Microsoft.ML.Trainers.FastTree
{
    /// <summary>
    /// The <see cref="IEstimator{TTransformer}"/> for training a decision tree ranking model using FastTree.
    /// </summary>
    /// <remarks>
    /// <format type="text/markdown"><![CDATA[
    /// To create this trainer, use [FastTree](xref:Microsoft.ML.TreeExtensions.FastTree(Microsoft.ML.RankingCatalog.RankingTrainers,System.String,System.String,System.String,System.String,System.Int32,System.Int32,System.Int32,System.Double))
    /// or [FastTree(Options)](xref:Microsoft.ML.TreeExtensions.FastTree(Microsoft.ML.RankingCatalog.RankingTrainers,Microsoft.ML.Trainers.FastTree.FastTreeRankingTrainer.Options)).
    ///
    /// [!include[io](~/../docs/samples/docs/api-reference/io-columns-ranking.md)]
    ///
    /// ### Trainer Characteristics
    /// |  |  |
    /// | -- | -- |
    /// | Machine learning task | Ranking |
    /// | Is normalization required? | No |
    /// | Is caching required? | No |
    /// | Required NuGet in addition to Microsoft.ML | Microsoft.ML.FastTree |
    /// | Exportable to ONNX | No |
    ///
    /// [!include[algorithm](~/../docs/samples/docs/api-reference/algo-details-fasttree.md)]
    /// ]]>
    /// </format>
    /// </remarks>
    /// <seealso cref="TreeExtensions.FastTree(RankingCatalog.RankingTrainers, string, string, string, string, int, int, int, double)"/>
    /// <seealso cref="TreeExtensions.FastTree(RegressionCatalog.RegressionTrainers, FastTreeRegressionTrainer.Options)"/>
    /// <seealso cref="Options"/>
    public sealed partial class FastTreeRankingTrainer
        : BoostingFastTreeTrainerBase<FastTreeRankingTrainer.Options, RankingPredictionTransformer<FastTreeRankingModelParameters>, FastTreeRankingModelParameters>
    {
        internal const string LoadNameValue = "FastTreeRanking";
        internal const string UserNameValue = "FastTree (Boosted Trees) Ranking";
        internal const string Summary = "Trains gradient boosted decision trees to the LambdaRank quasi-gradient.";
        internal const string ShortName = "ftrank";

        private IEnsembleCompressor<short> _ensembleCompressor;
        private Test _specialTrainSetTest;
        private TestHistory _firstTestSetHistory;

        /// <summary>
        /// The prediction kind for this trainer.
        /// </summary>
        private protected override PredictionKind PredictionKind => PredictionKind.Ranking;

        /// <summary>
        /// Initializes a new instance of <see cref="FastTreeRankingTrainer"/>
        /// </summary>
        /// <param name="env">The private instance of <see cref="IHostEnvironment"/>.</param>
        /// <param name="labelColumnName">The name of the label column.</param>
        /// <param name="featureColumnName">The name of the feature column.</param>
        /// <param name="rowGroupColumnName">The name for the column containing the group ID. </param>
        /// <param name="exampleWeightColumnName">The name for the column containing the examle weight.</param>
        /// <param name="numberOfLeaves">The max number of leaves in each regression tree.</param>
        /// <param name="numberOfTrees">Total number of decision trees to create in the ensemble.</param>
        /// <param name="minimumExampleCountPerLeaf">The minimal number of examples allowed in a leaf of a regression tree, out of the subsampled data.</param>
        /// <param name="learningRate">The learning rate.</param>
        internal FastTreeRankingTrainer(IHostEnvironment env,
            string labelColumnName = DefaultColumnNames.Label,
            string featureColumnName = DefaultColumnNames.Features,
            string rowGroupColumnName = DefaultColumnNames.GroupId,
            string exampleWeightColumnName = null,
            int numberOfLeaves = Defaults.NumberOfLeaves,
            int numberOfTrees = Defaults.NumberOfTrees,
            int minimumExampleCountPerLeaf = Defaults.MinimumExampleCountPerLeaf,
            double learningRate = Defaults.LearningRate)
            : base(env, TrainerUtils.MakeR4ScalarColumn(labelColumnName), featureColumnName, exampleWeightColumnName, rowGroupColumnName, numberOfLeaves, numberOfTrees, minimumExampleCountPerLeaf, learningRate)
        {
            Host.CheckNonEmpty(rowGroupColumnName, nameof(rowGroupColumnName));
        }

        /// <summary>
        /// Initializes a new instance of <see cref="FastTreeRankingTrainer"/> by using the <see cref="Options"/> class.
        /// </summary>
        /// <param name="env">The instance of <see cref="IHostEnvironment"/>.</param>
        /// <param name="options">Algorithm advanced settings.</param>
        internal FastTreeRankingTrainer(IHostEnvironment env, Options options)
        : base(env, options, TrainerUtils.MakeR4ScalarColumn(options.LabelColumnName))
        {
        }

        private protected override void CheckLabelCompatible(SchemaShape.Column labelCol)
        {
            Contracts.Assert(labelCol.IsValid);

            Action error =
                () => throw Host.ExceptSchemaMismatch(nameof(labelCol), "label", labelCol.Name, "Single or Key", labelCol.GetTypeString());

            if (labelCol.Kind != SchemaShape.Column.VectorKind.Scalar)
                error();
            if (!labelCol.IsKey && labelCol.ItemType != NumberDataViewType.Single)
                error();
        }

        private protected override float GetMaxLabel()
        {
            return GetLabelGains().Length - 1;
        }

        private protected override FastTreeRankingModelParameters TrainModelCore(TrainContext context)
        {
            Host.CheckValue(context, nameof(context));
            var trainData = context.TrainingSet;
            ValidData = context.ValidationSet;
            TestData = context.TestSet;

            using (var ch = Host.Start("Training"))
            {
                var maxLabel = GetLabelGains().Length - 1;
                ConvertData(trainData);
                TrainCore(ch);
                FeatureCount = trainData.Schema.Feature.Value.Type.GetValueCount();
            }
            return new FastTreeRankingModelParameters(Host, TrainedEnsemble, FeatureCount, InnerOptions);
        }

        private Double[] GetLabelGains()
        {
            try
            {
                Host.AssertValue(FastTreeTrainerOptions.CustomGains);
                return FastTreeTrainerOptions.CustomGains;
            }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is OverflowException)
                    throw Host.Except(ex, "Error in the format of custom gains. Inner exception is {0}", ex.Message);
                throw;
            }
        }

        private protected override void CheckOptions(IChannel ch)
        {
            if (FastTreeTrainerOptions.CustomGains != null)
            {
                var gains = FastTreeTrainerOptions.CustomGains;
                if (gains.Length < 5)
                {
                    throw ch.ExceptUserArg(nameof(FastTreeTrainerOptions.CustomGains),
                        "Has {0} gain levels. We require at least 5 elements.",
                        gains.Length);
                }
                DcgCalculator.LabelGainMap = gains;
                Dataset.DatasetSkeleton.LabelGainMap = gains;
            }

            bool doEarlyStop = FastTreeTrainerOptions.EarlyStoppingRuleFactory != null ||
                FastTreeTrainerOptions.EnablePruning;

            if (doEarlyStop)
                ch.CheckUserArg(FastTreeTrainerOptions.EarlyStoppingMetrics == 1 || FastTreeTrainerOptions.EarlyStoppingMetrics == 3,
                    nameof(FastTreeTrainerOptions.EarlyStoppingMetrics), "should be 1 or 3.");

            base.CheckOptions(ch);
        }

        private protected override void Initialize(IChannel ch)
        {
            base.Initialize(ch);
            if (FastTreeTrainerOptions.CompressEnsemble)
            {
                _ensembleCompressor = new LassoBasedEnsembleCompressor();
                _ensembleCompressor.Initialize(FastTreeTrainerOptions.NumberOfTrees, TrainSet, TrainSet.Ratings, FastTreeTrainerOptions.Seed);
            }
        }

        private protected override ObjectiveFunctionBase ConstructObjFunc(IChannel ch)
        {
            return new LambdaRankObjectiveFunction(TrainSet, TrainSet.Ratings, FastTreeTrainerOptions, ParallelTraining);
        }

        private protected override OptimizationAlgorithm ConstructOptimizationAlgorithm(IChannel ch)
        {
            OptimizationAlgorithm optimizationAlgorithm = base.ConstructOptimizationAlgorithm(ch);
            if (FastTreeTrainerOptions.UseLineSearch)
            {
                _specialTrainSetTest = new FastNdcgTest(optimizationAlgorithm.TrainingScores, TrainSet.Ratings, FastTreeTrainerOptions.SortingAlgorithm, FastTreeTrainerOptions.EarlyStoppingMetrics);
                optimizationAlgorithm.AdjustTreeOutputsOverride = new LineSearch(_specialTrainSetTest, 0, FastTreeTrainerOptions.MaximumNumberOfLineSearchSteps, FastTreeTrainerOptions.MinimumStepSize);
            }
            return optimizationAlgorithm;
        }

        private protected override BaggingProvider CreateBaggingProvider()
        {
            Host.Assert(FastTreeTrainerOptions.BaggingSize > 0);
            return new RankingBaggingProvider(TrainSet, FastTreeTrainerOptions.NumberOfLeaves, FastTreeTrainerOptions.Seed, FastTreeTrainerOptions.BaggingExampleFraction);
        }

        private protected override void PrepareLabels(IChannel ch)
        {
        }

        private protected override Test ConstructTestForTrainingData()
        {
            return new NdcgTest(ConstructScoreTracker(TrainSet), TrainSet.Ratings, FastTreeTrainerOptions.SortingAlgorithm);
        }

        private protected override void InitializeTests()
        {
            if (FastTreeTrainerOptions.TestFrequency != int.MaxValue)
            {
                AddFullTests();
            }

            if (FastTreeTrainerOptions.PrintTestGraph)
            {
                // If FirstTestHistory is null (which means the tests were not initialized due to /tf==infinity)
                // We need initialize first set for graph printing
                // Adding to a tests would result in printing the results after final iteration
                if (_firstTestSetHistory == null)
                {
                    var firstTestSetTest = CreateFirstTestSetTest();
                    _firstTestSetHistory = new TestHistory(firstTestSetTest, 0);
                }
            }

            // Tests for early stopping.
            TrainTest = CreateSpecialTrainSetTest();
            if (ValidSet != null)
                ValidTest = CreateSpecialValidSetTest();

            if (FastTreeTrainerOptions.PrintTrainValidGraph && FastTreeTrainerOptions.EnablePruning && _specialTrainSetTest == null)
            {
                _specialTrainSetTest = CreateSpecialTrainSetTest();
            }

            if (FastTreeTrainerOptions.EnablePruning && ValidTest != null)
            {
                if (!FastTreeTrainerOptions.UseTolerantPruning)
                {
                    //use simple eraly stopping condition
                    PruningTest = new TestHistory(ValidTest, 0);
                }
                else
                {
                    //use tolerant stopping condition
                    PruningTest = new TestWindowWithTolerance(ValidTest, 0, FastTreeTrainerOptions.PruningWindowSize, FastTreeTrainerOptions.PruningThreshold);
                }
            }
        }

        private void AddFullTests()
        {
            Tests.Add(CreateStandardTest(TrainSet));

            if (ValidSet != null)
            {
                Test test = CreateStandardTest(ValidSet);
                Tests.Add(test);
            }

            for (int t = 0; TestSets != null && t < TestSets.Length; ++t)
            {
                Test test = CreateStandardTest(TestSets[t]);
                if (t == 0)
                {
                    _firstTestSetHistory = new TestHistory(test, 0);
                }

                Tests.Add(test);
            }
        }

        private protected override void PrintIterationMessage(IChannel ch, IProgressChannel pch)
        {
            // REVIEW: Shift to using progress channels to report this information.
#if OLD_TRACE
            // This needs to be executed every iteration.
            if (PruningTest != null)
            {
                if (PruningTest is TestWindowWithTolerance)
                {
                    if (PruningTest.BestIteration != -1)
                        ch.Info("Iteration {0} \t(Best tolerated validation moving average NDCG@{1} {2}:{3:00.00}~{4:00.00})",
                                Ensemble.NumTrees,
                                _args.earlyStoppingMetrics,
                                PruningTest.BestIteration,
                                100 * (PruningTest as TestWindowWithTolerance).BestAverageValue,
                                100 * (PruningTest as TestWindowWithTolerance).CurrentAverageValue);
                    else
                        ch.Info("Iteration {0}", Ensemble.NumTrees);
                }
                else
                {
                    ch.Info("Iteration {0} \t(best validation NDCG@{1} {2}:{3:00.00}>{4:00.00})",
                            Ensemble.NumTrees,
                            _args.earlyStoppingMetrics,
                            PruningTest.BestIteration,
                            100 * PruningTest.BestResult.FinalValue,
                            100 * PruningTest.ComputeTests().First().FinalValue);
                }
            }
            else
                base.PrintIterationMessage(ch, pch);
#else
            base.PrintIterationMessage(ch, pch);
#endif
        }

        private protected override void ComputeTests()
        {
            if (_firstTestSetHistory != null)
                _firstTestSetHistory.ComputeTests();

            if (_specialTrainSetTest != null)
                _specialTrainSetTest.ComputeTests();

            if (PruningTest != null)
                PruningTest.ComputeTests();
        }

        private protected override string GetTestGraphLine()
        {
            StringBuilder lineBuilder = new StringBuilder();

            lineBuilder.AppendFormat("Eval:\tnet.{0:D8}.ini", Ensemble.NumTrees - 1);

            foreach (var r in _firstTestSetHistory.ComputeTests())
            {
                lineBuilder.AppendFormat("\t{0:0.0000}", r.FinalValue);
            }

            double trainTestResult = 0.0;
            double validTestResult = 0.0;

            // We only print non-zero train&valid graph if earlyStoppingTruncation!=0
            // In case /es is not set, we print 0 for train and valid graph NDCG
            // Let's keeping this behaviour for backward compatibility with previous FR version
            // Ideally /graphtv should enforce non-zero /es in the commandline validation
            if (_specialTrainSetTest != null)
            {
                trainTestResult = _specialTrainSetTest.ComputeTests().First().FinalValue;
            }

            if (PruningTest != null)
            {
                validTestResult = PruningTest.ComputeTests().First().FinalValue;
            }

            lineBuilder.AppendFormat("\t{0:0.0000}\t{1:0.0000}", trainTestResult, validTestResult);

            return lineBuilder.ToString();
        }

        private protected override void Train(IChannel ch)
        {
            base.Train(ch);
            // Print final last iteration.
            // Note that trainNDCG printed in graph will be from copy of a value from previous iteration
            // and will diffre slightly from the proper final value computed by FullTest.
            // We cannot compute the final NDCG here due to the fact we use FastNDCGTestForTrainSet computing NDCG based on label sort saved during gradient computation (and we don;t have gradients for n+1 iteration)
            // Keeping it in sync with original FR code
            PrintTestGraph(ch);
        }

        private protected override void CustomizedTrainingIteration(InternalRegressionTree tree)
        {
            Contracts.AssertValueOrNull(tree);
            if (tree != null && FastTreeTrainerOptions.CompressEnsemble)
            {
                double[] trainOutputs = Ensemble.GetTreeAt(Ensemble.NumTrees - 1).GetOutputs(TrainSet);
                _ensembleCompressor.SetTreeScores(Ensemble.NumTrees - 1, trainOutputs);
            }
        }

        /// <summary>
        /// Create standard test for dataset.
        /// </summary>
        /// <param name="dataset">dataset used for testing</param>
        /// <returns>standard test for the dataset</returns>
        private Test CreateStandardTest(Dataset dataset)
        {
            if (Utils.Size(dataset.MaxDcg) == 0)
                dataset.Skeleton.RecomputeMaxDcg(10);

            return new NdcgTest(
                ConstructScoreTracker(dataset),
                dataset.Ratings,
                FastTreeTrainerOptions.SortingAlgorithm);
        }

        /// <summary>
        /// Create the special test for train set.
        /// </summary>
        /// <returns>test for train set</returns>
        private Test CreateSpecialTrainSetTest()
        {
            return new FastNdcgTestForTrainSet(
                OptimizationAlgorithm.TrainingScores,
                OptimizationAlgorithm.ObjectiveFunction as LambdaRankObjectiveFunction,
                TrainSet.Ratings,
                FastTreeTrainerOptions.SortingAlgorithm,
                FastTreeTrainerOptions.EarlyStoppingMetrics);
        }

        /// <summary>
        /// Create the special test for valid set.
        /// </summary>
        /// <returns>test for train set</returns>
        private Test CreateSpecialValidSetTest()
        {
            return new FastNdcgTest(
                ConstructScoreTracker(ValidSet),
                ValidSet.Ratings,
                FastTreeTrainerOptions.SortingAlgorithm,
                FastTreeTrainerOptions.EarlyStoppingMetrics);
        }

        /// <summary>
        /// Create the test for the first test set.
        /// </summary>
        /// <returns>test for the first test set</returns>
        private Test CreateFirstTestSetTest()
        {
            return CreateStandardTest(TestSets[0]);
        }

        /// <summary>
        /// Get the header of test graph
        /// </summary>
        /// <returns>Test graph header</returns>
        private protected override string GetTestGraphHeader()
        {
            StringBuilder headerBuilder = new StringBuilder("Eval:\tFileName\tNDCG@1\tNDCG@2\tNDCG@3\tNDCG@4\tNDCG@5\tNDCG@6\tNDCG@7\tNDCG@8\tNDCG@9\tNDCG@10");

            if (FastTreeTrainerOptions.PrintTrainValidGraph)
            {
                headerBuilder.Append("\tNDCG@20\tNDCG@40");
                headerBuilder.AppendFormat(
                    "\nNote: Printing train NDCG@{0} as NDCG@20 and validation NDCG@{0} as NDCG@40..\n",
                    FastTreeTrainerOptions.EarlyStoppingMetrics);
            }

            return headerBuilder.ToString();
        }

        private protected override RankingPredictionTransformer<FastTreeRankingModelParameters> MakeTransformer(FastTreeRankingModelParameters model, DataViewSchema trainSchema)
        => new RankingPredictionTransformer<FastTreeRankingModelParameters>(Host, model, trainSchema, FeatureColumn.Name);

        /// <summary>
        /// Trains a <see cref="FastTreeRankingTrainer"/> using both training and validation data, returns
        /// a <see cref="RankingPredictionTransformer{FastTreeRankingModelParameters}"/>.
        /// </summary>
        public RankingPredictionTransformer<FastTreeRankingModelParameters> Fit(IDataView trainData, IDataView validationData)
            => TrainTransformer(trainData, validationData);

        private protected override SchemaShape.Column[] GetOutputColumnsCore(SchemaShape inputSchema)
        {
            return new[]
           {
                new SchemaShape.Column(DefaultColumnNames.Score, SchemaShape.Column.VectorKind.Scalar, NumberDataViewType.Single, false, new SchemaShape(AnnotationUtils.GetTrainerOutputAnnotation()))
            };
        }

        internal sealed class LambdaRankObjectiveFunction : ObjectiveFunctionBase, IStepSearch
        {
            private readonly short[] _labels;

            private enum DupeIdInfo
            {
                NoInformation = 0,
                Unique = 1,
                FormatNotSupported = 1000000,
                Code404 = 1000001
            };

            // precomputed arrays
            private readonly double[] _inverseMaxDcgt;
            private readonly double[] _discount;
            private readonly int[] _oneTwoThree;

            private int[][] _labelCounts;

            // reusable memory, technical stuff
            private int[][] _permutationBuffers;
            private DcgPermutationComparer[] _comparers;

            //gains
            private double[] _gain;
            private double[] _gainLabels;

            // parameters
            private int _maxDcgTruncationLevel;
            private bool _useDcg;
            // A lookup table for the sigmoid used in the lambda calculation
            // Note: Is built for a specific sigmoid parameter, so assumes this will be constant throughout computation
            private double[] _sigmoidTable;
            private double _minScore;       // Computed: range of scores covered in table
            private double _maxScore;
            private double _minSigmoid;
            private double _maxSigmoid;
            private double _scoreToSigmoidTableFactor;
            private const double _expAsymptote = -50;     // exp( x < expAsymptote ) is assumed to be 0
            private const int _sigmoidBins = 1000000;         // Number of bins in the lookup table

            // Secondary gains, currently not used in any way.
#pragma warning disable 0649
            private double _secondaryMetricShare;
            private double[] _secondaryInverseMaxDcgt;
            private double[] _secondaryGains;
#pragma warning restore 0649

            // Baseline risk.
            private static int _iteration = 0; // This is a static class global member which keeps track of the iterations.
            private double _baselineAlphaCurrent;

            // These reusable buffers are used for
            // 1. preprocessing the scores for continuous cost function
            // 2. shifted NDCG
            // 3. max DCG per query
            private double[] _scoresCopy;
            private short[] _labelsCopy;
            private short[] _groupIdToTopLabel;

            // parameters
            private double _sigmoidParam;
            private char _costFunctionParam;
            private bool _filterZeroLambdas;

            private bool _distanceWeight2;
            private bool _normalizeQueryLambdas;
            private bool _useShiftedNdcg;
            private IParallelTraining _parallelTraining;

            // Used for training NDCG calculation
            // Keeps track of labels of top 3 documents per query
            public short[][] TrainQueriesTopLabels;

            public LambdaRankObjectiveFunction(Dataset trainset, short[] labels, Options options, IParallelTraining parallelTraining)
                : base(trainset,
                    options.LearningRate,
                    options.Shrinkage,
                    options.MaximumTreeOutput,
                    options.GetDerivativesSampleRate,
                    options.BestStepRankingRegressionTrees,
                    options.Seed)
            {

                _labels = labels;
                TrainQueriesTopLabels = new short[Dataset.NumQueries][];
                for (int q = 0; q < Dataset.NumQueries; ++q)
                    TrainQueriesTopLabels[q] = new short[3];

                _labelCounts = new int[Dataset.NumQueries][];
                int relevancyLevel = DcgCalculator.LabelGainMap.Length;
                for (int q = 0; q < Dataset.NumQueries; ++q)
                    _labelCounts[q] = new int[relevancyLevel];

                // precomputed arrays
                _maxDcgTruncationLevel = options.NdcgTruncationLevel;
                _useDcg = options.UseDcg;
                if (_useDcg)
                {
                    _inverseMaxDcgt = new double[Dataset.NumQueries];
                    for (int q = 0; q < Dataset.NumQueries; ++q)
                        _inverseMaxDcgt[q] = 1.0;
                }
                else
                {
                    _inverseMaxDcgt = DcgCalculator.MaxDcg(_labels, Dataset.Boundaries, _maxDcgTruncationLevel, _labelCounts);
                    for (int q = 0; q < Dataset.NumQueries; ++q)
                        _inverseMaxDcgt[q] = 1.0 / _inverseMaxDcgt[q];
                }

                _discount = new double[Dataset.MaxDocsPerQuery];
                FillDiscounts(options.PositionDiscountFreeform);

                _oneTwoThree = new int[Dataset.MaxDocsPerQuery];
                for (int d = 0; d < Dataset.MaxDocsPerQuery; ++d)
                    _oneTwoThree[d] = d;

                // reusable resources
                int numThreads = BlockingThreadPool.NumThreads;
                _comparers = new DcgPermutationComparer[numThreads];
                for (int i = 0; i < numThreads; ++i)
                    _comparers[i] = DcgPermutationComparerFactory.GetDcgPermutationFactory(options.SortingAlgorithm);

                _permutationBuffers = new int[numThreads][];
                for (int i = 0; i < numThreads; ++i)
                    _permutationBuffers[i] = new int[Dataset.MaxDocsPerQuery];

                _gain = Dataset.DatasetSkeleton.LabelGainMap;
                FillGainLabels();

                #region parameters
                _sigmoidParam = options.LearningRate;
                _costFunctionParam = options.CostFunctionParam;
                _distanceWeight2 = options.DistanceWeight2;
                _normalizeQueryLambdas = options.NormalizeQueryLambdas;

                _useShiftedNdcg = options.ShiftedNdcg;
                _filterZeroLambdas = options.FilterZeroLambdas;
                #endregion

                _scoresCopy = new double[Dataset.NumDocs];
                _labelsCopy = new short[Dataset.NumDocs];
                _groupIdToTopLabel = new short[Dataset.NumDocs];

                FillSigmoidTable(_sigmoidParam);
#if OLD_DATALOAD
            SetupSecondaryGains(cmd);
#endif
                _parallelTraining = parallelTraining;
            }

#if OLD_DATALOAD
        private void SetupSecondaryGains(Arguments args)
        {
            _secondaryGains = null;
            _secondaryMetricShare = args.secondaryMetricShare;
            _secondaryIsolabelExclusive = args.secondaryIsolabelExclusive;
            if (_secondaryMetricShare != 0.0)
            {
                _secondaryGains = Dataset.Skeleton.GetData<double>("SecondaryGains");
                if (_secondaryGains == null)
                {
                    _secondaryMetricShare = 0.0;
                    return;
                }
                _secondaryInverseMaxDCGT = DCGCalculator.MaxDCG(_secondaryGains, Dataset.Boundaries,
                    new int[] { args.lambdaMartMaxTruncation })[0].Select(d => 1.0 / d).ToArray();
            }
        }
#endif
            private void FillSigmoidTable(double sigmoidParam)
            {
                // minScore is such that 2*sigmoidParam*score is < expAsymptote if score < minScore
                _minScore = _expAsymptote / sigmoidParam / 2;
                _maxScore = -_minScore;

                _sigmoidTable = new double[_sigmoidBins];
                for (int i = 0; i < _sigmoidBins; i++)
                {
                    double score = (_maxScore - _minScore) / _sigmoidBins * i + _minScore;
                    if (score > 0.0)
                        _sigmoidTable[i] = 2.0 - 2.0 / (1.0 + Math.Exp(-2.0 * sigmoidParam * score));
                    else
                        _sigmoidTable[i] = 2.0 / (1.0 + Math.Exp(2.0 * sigmoidParam * score));
                }
                _scoreToSigmoidTableFactor = _sigmoidBins / (_maxScore - _minScore);
                _minSigmoid = _sigmoidTable[0];
                _maxSigmoid = _sigmoidTable.Last();
            }

            private void IgnoreNonBestDuplicates(short[] labels, double[] scores, int[] order, UInt32[] dupeIds, int begin, int numDocuments)
            {
                if (dupeIds == null || dupeIds.Length == 0)
                {
                    return;
                }

                // Reset top label for all groups
                for (int i = begin; i < begin + numDocuments; ++i)
                {
                    _groupIdToTopLabel[i] = -1;
                }

                for (int i = 0; i < numDocuments; ++i)
                {
                    Contracts.Check(0 <= order[i] && order[i] < numDocuments, "the index to document exceeds range");

                    int index = begin + order[i];

                    UInt32 group = dupeIds[index];
                    if (group == (UInt32)DupeIdInfo.Code404 || group == (UInt32)DupeIdInfo.FormatNotSupported ||
                        group == (UInt32)DupeIdInfo.Unique || group == (UInt32)DupeIdInfo.NoInformation)
                    {
                        continue;
                    }

                    // group starts from 2 (since 0 is unknown and 1 is unique)
                    Contracts.Check(2 <= group && group < numDocuments + 2, "dupeId group exceeds range");

                    UInt32 groupIndex = (UInt32)begin + group - 2;

                    if (_groupIdToTopLabel[groupIndex] != -1)
                    {
                        // this is the second+ occurrence of a result
                        // of the same duplicate group, so:
                        // - disconsider when applying the cost function
                        //
                        // Only do this if the rating of this dupe is worse or equal,
                        // otherwise we want this dupe to be pushed to the top
                        // so we keep it
                        if (labels[index] <= _groupIdToTopLabel[groupIndex])
                        {
                            labels[index] = 0;
                            scores[index] = double.MinValue;
                        }
                    }
                    else
                    {
                        _groupIdToTopLabel[groupIndex] = labels[index];
                    }
                }
            }

            public override double[] GetGradient(IChannel ch, double[] scores)
            {
                _baselineAlphaCurrent = 0.0;
                double[] grads = base.GetGradient(ch, scores);
                _iteration++;
                return grads;
            }

            protected override void GetGradientInOneQuery(int query, int threadIndex)
            {
                int begin = Dataset.Boundaries[query];
                int numDocuments = Dataset.Boundaries[query + 1] - Dataset.Boundaries[query];

                Array.Clear(Gradient, begin, numDocuments);
                Array.Clear(Weights, begin, numDocuments);

                double inverseMaxDcg = _inverseMaxDcgt[query];
                double secondaryInverseMaxDcg = _secondaryMetricShare == 0 ? 0.0 : _secondaryInverseMaxDcgt[query];

                int[] permutation = _permutationBuffers[threadIndex];

                short[] labels = _labels;
                double[] scoresToUse = Scores;

                if (_useShiftedNdcg)
                {
                    // Copy the labels for this query
                    Array.Copy(_labels, begin, _labelsCopy, begin, numDocuments);
                    labels = _labelsCopy;
                }

                if (_costFunctionParam == 'c' || _useShiftedNdcg)
                {
                    // Copy the scores for this query
                    Array.Copy(Scores, begin, _scoresCopy, begin, numDocuments);
                    scoresToUse = _scoresCopy;
                }

                // Keep track of top 3 labels for later use
                //GetTopQueryLabels(query, permutation, false);

                double lambdaSum = 0;

                unsafe
                {
                    fixed (int* pPermutation = permutation)
                    fixed (short* pLabels = labels)
                    fixed (double* pScores = scoresToUse)
                    fixed (double* pLambdas = Gradient)
                    fixed (double* pWeights = Weights)
                    fixed (double* pDiscount = _discount)
                    fixed (double* pGain = _gain)
                    fixed (double* pGainLabels = _gainLabels)
                    fixed (double* pSigmoidTable = _sigmoidTable)
                    fixed (double* pSecondaryGains = _secondaryGains)
                    fixed (int* pOneTwoThree = _oneTwoThree)
                    {
                        // calculates the permutation that orders "scores" in descending order, without modifying "scores"
                        Array.Copy(_oneTwoThree, permutation, numDocuments);

                        if (IntArray.UseFastTreeNative)
                        {
                            PermutationSort(permutation, scoresToUse, labels, numDocuments, begin);
                            // Get how far about baseline our current
                            double baselineDcgGap = 0.0;
                            //baselineDCGGap = ((new Random(query)).NextDouble() * 2 - 1)/inverseMaxDCG; // THIS IS EVIL CODE REMOVE LATER
                            // Keep track of top 3 labels for later use
                            GetTopQueryLabels(query, permutation, true);

                            if (_useShiftedNdcg)
                            {
                                // Set non-best (rank-wise) duplicates to be ignored. Set Score to MinValue, Label to 0
                                IgnoreNonBestDuplicates(labels, scoresToUse, permutation, Dataset.DupeIds, begin, numDocuments);
                            }

                            int numActualResults = numDocuments;

                            // If the const function is ContinuousWeightedRanknet, update output scores
                            if (_costFunctionParam == 'c')
                            {
                                for (int i = begin; i < begin + numDocuments; ++i)
                                {
                                    if (pScores[i] == double.MinValue)
                                    {
                                        numActualResults--;
                                    }
                                    else
                                    {
                                        pScores[i] = pScores[i] * (1.0 - pLabels[i] * 1.0 / (20.0 * Dataset.DatasetSkeleton.LabelGainMap.Length));
                                    }
                                }
                            }

                            // Continuous cost function and shifted NDCG require a re-sort and recomputation of maxDCG
                            // (Change of scores in the former and scores and labels in the latter)
                            if (!_useDcg && (_costFunctionParam == 'c' || _useShiftedNdcg))
                            {
                                PermutationSort(permutation, scoresToUse, labels, numDocuments, begin);
                                inverseMaxDcg = 1.0 / DcgCalculator.MaxDcgQuery(labels, begin, numDocuments, numDocuments, _labelCounts[query]);
                            }
                            // A constant related to secondary labels, which does not exist in the current codebase.
                            const bool secondaryIsolabelExclusive = false;
                            GetDerivatives(numDocuments, begin, pPermutation, pLabels,
                                    pScores, pLambdas, pWeights, pDiscount,
                                    inverseMaxDcg, pGainLabels,
                                    _secondaryMetricShare, secondaryIsolabelExclusive, secondaryInverseMaxDcg, pSecondaryGains,
                                    pSigmoidTable, _minScore, _maxScore, _sigmoidTable.Length, _scoreToSigmoidTableFactor,
                                    _costFunctionParam, _distanceWeight2, numActualResults, &lambdaSum, double.MinValue,
                                    _baselineAlphaCurrent, baselineDcgGap);
                        }
                        else
                        {
                            if (_useShiftedNdcg || _costFunctionParam == 'c' || _distanceWeight2 || _normalizeQueryLambdas)
                            {
                                throw new Exception("Shifted NDCG / ContinuousWeightedRanknet / distanceWeight2 / normalized lambdas are only supported by unmanaged code");
                            }

                            var comparer = _comparers[threadIndex];
                            comparer.Scores = scoresToUse;
                            comparer.Labels = labels;
                            comparer.ScoresOffset = begin;
                            comparer.LabelsOffset = begin;
                            Array.Sort(permutation, 0, numDocuments, comparer);

                            // go over all pairs
                            double scoreHighMinusLow;
                            double lambdaP;
                            double weightP;
                            double deltaNdcgP;
                            for (int i = 0; i < numDocuments; ++i)
                            {
                                int high = begin + pPermutation[i];
                                if (pLabels[high] == 0)
                                    continue;
                                double deltaLambdasHigh = 0;
                                double deltaWeightsHigh = 0;

                                for (int j = 0; j < numDocuments; ++j)
                                {
                                    // only consider pairs with different labels, where "high" has a higher label than "low"
                                    if (i == j)
                                        continue;
                                    int low = begin + pPermutation[j];
                                    if (pLabels[high] <= pLabels[low])
                                        continue;

                                    // calculate the lambdaP for this pair
                                    scoreHighMinusLow = pScores[high] - pScores[low];

                                    if (scoreHighMinusLow <= _minScore)
                                        lambdaP = _minSigmoid;
                                    else if (scoreHighMinusLow >= _maxScore)
                                        lambdaP = _maxSigmoid;
                                    else
                                        lambdaP = _sigmoidTable[(int)((scoreHighMinusLow - _minScore) * _scoreToSigmoidTableFactor)];

                                    weightP = lambdaP * (2.0 - lambdaP);

                                    // calculate the deltaNDCGP for this pair
                                    deltaNdcgP =
                                        (pGain[pLabels[high]] - pGain[pLabels[low]]) *
                                        Math.Abs((pDiscount[i] - pDiscount[j])) *
                                        inverseMaxDcg;

                                    // update lambdas and weights
                                    deltaLambdasHigh += lambdaP * deltaNdcgP;
                                    pLambdas[low] -= lambdaP * deltaNdcgP;
                                    deltaWeightsHigh += weightP * deltaNdcgP;
                                    pWeights[low] += weightP * deltaNdcgP;
                                }
                                pLambdas[high] += deltaLambdasHigh;
                                pWeights[high] += deltaWeightsHigh;
                            }
                        }

                        if (_normalizeQueryLambdas)
                        {
                            if (lambdaSum > 0)
                            {
                                double normFactor = (10 * Math.Log(1 + lambdaSum)) / lambdaSum;

                                for (int i = begin; i < begin + numDocuments; ++i)
                                {
                                    pLambdas[i] = pLambdas[i] * normFactor;
                                    pWeights[i] = pWeights[i] * normFactor;
                                }
                            }
                        }
                    }
                }
            }

            void IStepSearch.AdjustTreeOutputs(IChannel ch, InternalRegressionTree tree, DocumentPartitioning partitioning,
                                            ScoreTracker trainingScores)
            {
                const double epsilon = 1.4e-45;
                double[] means = null;
                if (!BestStepRankingRegressionTrees)
                    means = _parallelTraining.GlobalMean(Dataset, tree, partitioning, Weights, _filterZeroLambdas);
                for (int l = 0; l < tree.NumLeaves; ++l)
                {
                    double output = tree.LeafValue(l);
                    if (!BestStepRankingRegressionTrees)
                        output = (output + epsilon) / (2.0 * means[l] + epsilon);

                    if (output > MaxTreeOutput)
                        output = MaxTreeOutput;
                    else if (output < -MaxTreeOutput)
                        output = -MaxTreeOutput;

                    tree.SetLeafValue(l, output);
                }
            }

            private void FillDiscounts(string positionDiscountFreeform)
            {
                if (positionDiscountFreeform == null)
                {
                    for (int d = 0; d < Dataset.MaxDocsPerQuery; ++d)
                        _discount[d] = 1.0 / Math.Log(2.0 + d);
                }
            }

            private void FillGainLabels()
            {
                _gainLabels = new double[Dataset.NumDocs];
                for (int i = 0; i < Dataset.NumDocs; i++)
                {
                    _gainLabels[i] = _gain[_labels[i]];
                }
            }

            // Keep track of top 3 labels for later use.
            private void GetTopQueryLabels(int query, int[] permutation, bool bAlreadySorted)
            {
                int numDocuments = Dataset.Boundaries[query + 1] - Dataset.Boundaries[query];
                int begin = Dataset.Boundaries[query];

                if (!bAlreadySorted)
                {
                    // calculates the permutation that orders "scores" in descending order, without modifying "scores"
                    Array.Copy(_oneTwoThree, permutation, numDocuments);
                    PermutationSort(permutation, Scores, _labels, numDocuments, begin);
                }

                for (int i = 0; i < 3 && i < numDocuments; ++i)
                    TrainQueriesTopLabels[query][i] = _labels[begin + permutation[i]];
            }

            private static void PermutationSort(int[] permutation, double[] scores, short[] labels, int numDocs, int shift)
            {
                Contracts.AssertValue(permutation);
                Contracts.AssertValue(scores);
                Contracts.AssertValue(labels);
                Contracts.Assert(numDocs > 0);
                Contracts.Assert(shift >= 0);
                Contracts.Assert(scores.Length - numDocs >= shift);
                Contracts.Assert(labels.Length - numDocs >= shift);

                Array.Sort(permutation, 0, numDocs,
                    Comparer<int>.Create((x, y) =>
                    {
                        if (scores[shift + x] > scores[shift + y])
                            return -1;
                        if (scores[shift + x] < scores[shift + y])
                            return 1;
                        if (labels[shift + x] < labels[shift + y])
                            return -1;
                        if (labels[shift + x] > labels[shift + y])
                            return 1;
                        return x - y;
                    }));
            }

            [DllImport("FastTreeNative", EntryPoint = "C_GetDerivatives", CharSet = CharSet.Ansi), SuppressUnmanagedCodeSecurity]
            private static extern unsafe void GetDerivatives(
                int numDocuments, int begin, int* pPermutation, short* pLabels,
                double* pScores, double* pLambdas, double* pWeights, double* pDiscount,
                double inverseMaxDcg, double* pGainLabels,
                double secondaryMetricShare, [MarshalAs(UnmanagedType.U1)] bool secondaryExclusive, double secondaryInverseMaxDcg, double* pSecondaryGains,
                double* lambdaTable, double minScore, double maxScore,
                int lambdaTableLength, double scoreToLambdaTableFactor,
                char costFunctionParam, [MarshalAs(UnmanagedType.U1)] bool distanceWeight2, int numActualDocuments,
                double* pLambdaSum, double doubleMinValue, double alphaRisk, double baselineVersusCurrentDcg);

        }
    }

    /// <summary>
    /// Model parameters for <see cref="FastTreeRankingTrainer"/>.
    /// </summary>
    public sealed class FastTreeRankingModelParameters : TreeEnsembleModelParametersBasedOnRegressionTree
    {
        internal const string LoaderSignature = "FastTreeRankerExec";
        internal const string RegistrationName = "FastTreeRankingPredictor";

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "FTREE RA",
                // verWrittenCur: 0x00010001, // Initial
                // verWrittenCur: 0x00010002, // _numFeatures serialized
                // verWrittenCur: 0x00010003, // Ini content out of predictor
                // verWrittenCur: 0x00010004, // Add _defaultValueForMissing
                verWrittenCur: 0x00010005, // Categorical splits.
                verReadableCur: 0x00010004,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(FastTreeRankingModelParameters).Assembly.FullName);
        }

        private protected override uint VerNumFeaturesSerialized => 0x00010002;

        private protected override uint VerDefaultValueSerialized => 0x00010004;

        private protected override uint VerCategoricalSplitSerialized => 0x00010005;

        internal FastTreeRankingModelParameters(IHostEnvironment env, InternalTreeEnsemble trainedEnsemble, int featureCount, string innerArgs)
            : base(env, RegistrationName, trainedEnsemble, featureCount, innerArgs)
        {
        }

        private FastTreeRankingModelParameters(IHostEnvironment env, ModelLoadContext ctx)
            : base(env, RegistrationName, ctx, GetVersionInfo())
        {
        }

        private protected override void SaveCore(ModelSaveContext ctx)
        {
            base.SaveCore(ctx);
            ctx.SetVersionInfo(GetVersionInfo());
        }

        internal static FastTreeRankingModelParameters Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            return new FastTreeRankingModelParameters(env, ctx);
        }

        private protected override PredictionKind PredictionKind => PredictionKind.Ranking;
    }

    internal static partial class FastTree
    {
        [TlcModule.EntryPoint(Name = "Trainers.FastTreeRanker",
            Desc = FastTreeRankingTrainer.Summary,
            UserName = FastTreeRankingTrainer.UserNameValue,
            ShortName = FastTreeRankingTrainer.ShortName)]
        public static CommonOutputs.RankingOutput TrainRanking(IHostEnvironment env, FastTreeRankingTrainer.Options input)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register("TrainFastTree");
            host.CheckValue(input, nameof(input));
            EntryPointUtils.CheckInputArgs(host, input);

            return TrainerEntryPointsUtils.Train<FastTreeRankingTrainer.Options, CommonOutputs.RankingOutput>(host, input,
                () => new FastTreeRankingTrainer(host, input),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.LabelColumnName),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.ExampleWeightColumnName),
                () => TrainerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.RowGroupColumnName));
        }
    }
}
