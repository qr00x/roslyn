﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.EmbeddedLanguages;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Classification
{
    internal abstract class AbstractEmbeddedLanguageClassificationService :
        AbstractEmbeddedLanguageFeatureService<IEmbeddedLanguageClassifier>, IEmbeddedLanguageClassificationService
    {
        /// <summary>
        /// Finally classifier to run if there is no embedded language in a string.  It will just classify escape sequences.
        /// </summary>
        private readonly IEmbeddedLanguageClassifier _fallbackClassifier;

        protected AbstractEmbeddedLanguageClassificationService(
            string languageName,
            EmbeddedLanguageInfo info,
            ISyntaxKinds syntaxKinds,
            IEmbeddedLanguageClassifier fallbackClassifier,
            IEnumerable<Lazy<IEmbeddedLanguageClassifier, EmbeddedLanguageMetadata>> allClassifiers)
            : base(languageName, info, syntaxKinds, allClassifiers)
        {
            _fallbackClassifier = fallbackClassifier;
        }

        public async Task AddEmbeddedLanguageClassificationsAsync(
            Document document, TextSpan textSpan, ClassificationOptions options, SegmentedList<ClassifiedSpan> result, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            AddEmbeddedLanguageClassifications(document.Project, semanticModel, textSpan, options, result, cancellationToken);
        }

        public void AddEmbeddedLanguageClassifications(
            Project? project, SemanticModel semanticModel, TextSpan textSpan, ClassificationOptions options, SegmentedList<ClassifiedSpan> result, CancellationToken cancellationToken)
        {
            if (project is null)
                return;

            var root = semanticModel.SyntaxTree.GetRoot(cancellationToken);
            var worker = new Worker(this, project, semanticModel, textSpan, options, result, cancellationToken);
            worker.VisitTokens(root);
        }

        private readonly ref struct Worker(
            AbstractEmbeddedLanguageClassificationService service,
            Project project,
            SemanticModel semanticModel,
            TextSpan textSpan,
            ClassificationOptions options,
            SegmentedList<ClassifiedSpan> result,
            CancellationToken cancellationToken)
        {
            private readonly AbstractEmbeddedLanguageClassificationService _owner = service;
            private readonly Project _project = project;
            private readonly SemanticModel _semanticModel = semanticModel;
            private readonly TextSpan _textSpan = textSpan;
            private readonly ClassificationOptions _options = options;
            private readonly SegmentedList<ClassifiedSpan> _result = result;
            private readonly CancellationToken _cancellationToken = cancellationToken;

            public void VisitTokens(SyntaxNode node)
            {
                using var pooledStack = SharedPools.Default<Stack<SyntaxNodeOrToken>>().GetPooledObject();
                var stack = pooledStack.Object;
                stack.Push(node);
                while (!stack.IsEmpty())
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var currentNodeOrToken = stack.Pop();
                    if (currentNodeOrToken.Span.IntersectsWith(_textSpan))
                    {
                        if (currentNodeOrToken.IsNode)
                        {
                            foreach (var child in currentNodeOrToken.ChildNodesAndTokens().Reverse())
                            {
                                stack.Push(child);
                            }
                        }
                        else
                        {
                            ProcessToken(currentNodeOrToken.AsToken());
                        }
                    }
                }
            }

            private void ProcessToken(SyntaxToken token)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                ProcessTriviaList(token.LeadingTrivia);
                ClassifyToken(token);
                ProcessTriviaList(token.TrailingTrivia);
            }

            private void ClassifyToken(SyntaxToken token)
            {
                if (token.Span.IntersectsWith(_textSpan) && _owner.SyntaxTokenKinds.Contains(token.RawKind))
                {
                    var context = new EmbeddedLanguageClassificationContext(
                        _project, _semanticModel, token, _options, _owner.Info.VirtualCharService, _result, _cancellationToken);

                    var classifiers = _owner.GetServices(_semanticModel, token, _cancellationToken);
                    foreach (var classifier in classifiers)
                    {
                        // If this classifier added values then need to check the other ones.
                        if (TryClassify(classifier.Value, context))
                            return;
                    }

                    // If not other classifier classified this, then give the fallback classifier a chance to classify basic language escapes.
                    TryClassify(_owner._fallbackClassifier, context);
                }
            }

            private bool TryClassify(IEmbeddedLanguageClassifier classifier, EmbeddedLanguageClassificationContext context)
            {
                var count = _result.Count;
                classifier.RegisterClassifications(context);
                return _result.Count != count;
            }

            private void ProcessTriviaList(SyntaxTriviaList triviaList)
            {
                foreach (var trivia in triviaList)
                    ProcessTrivia(trivia);
            }

            private void ProcessTrivia(SyntaxTrivia trivia)
            {
                if (trivia.HasStructure && trivia.FullSpan.IntersectsWith(_textSpan))
                    VisitTokens(trivia.GetStructure()!);
            }
        }
    }
}
