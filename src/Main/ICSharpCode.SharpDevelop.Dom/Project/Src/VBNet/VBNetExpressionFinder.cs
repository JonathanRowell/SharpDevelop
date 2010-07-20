﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Siegfried Pammer" email="siegfriedpammer@gmail.com" />
//     <version>$Revision$</version>
// </file>

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.NRefactory;
using ICSharpCode.NRefactory.Parser;
using ICSharpCode.NRefactory.Parser.VB;

namespace ICSharpCode.SharpDevelop.Dom.VBNet
{
	/// <summary>
	/// Description of VBNetExpressionFinder.
	/// </summary>
	public class VBNetExpressionFinder : IExpressionFinder
	{
		ParseInformation parseInformation;
		IProjectContent projectContent;
		ILexer lexer;
		Location targetPosition;
		List<int> lineOffsets;
		
		int LocationToOffset(Location location)
		{
			if (location.Line <= 0) return -1;
			return lineOffsets[location.Line - 1] + location.Column - 1;
		}
		
		Location OffsetToLocation(int offset)
		{
			int lineNumber = lineOffsets.BinarySearch(offset);
			if (lineNumber < 0) {
				lineNumber = (~lineNumber) - 1;
			}
			return new Location(offset - lineOffsets[lineNumber] + 1, lineNumber + 1);
		}
		
		public VBNetExpressionFinder(ParseInformation parseInformation)
		{
			this.parseInformation = parseInformation;
			if (parseInformation != null && parseInformation.CompilationUnit != null) {
				projectContent = parseInformation.CompilationUnit.ProjectContent;
			} else {
				projectContent = DefaultProjectContent.DummyProjectContent;
			}
		}
		
		public ExpressionResult FindExpression(string text, int offset)
		{
			Init(text, offset);
			
			ExpressionFinder p = new ExpressionFinder();
			lexer = ParserFactory.CreateLexer(SupportedLanguage.VBNet, new StringReader(text));
			Token t = lexer.NextToken();
			
			while (t.EndLocation < targetPosition) {
				p.InformToken(t);
				t = lexer.NextToken();
			}
			
			if (t.EndLocation == targetPosition && ((t.Kind <= Tokens.ColonAssign && t.Kind > Tokens.Identifier) || t.Kind == Tokens.EOL)) {
				p.InformToken(t);
				t = lexer.NextToken();
			}
			
			p.Advance();
			
			var block = p.CurrentBlock;
			
			ExpressionContext context = p.IsIdentifierExpected && !p.IsMissingModifier ? ExpressionContext.IdentifierExpected : GetContext(block);
			
			if (t.Location < targetPosition) {
				p.InformToken(t);
				p.Advance();
			}
			
			if (p.Errors.Any()) {
				foreach (var e in p.Errors)
					LoggingService.Warn("not expected: " + e);
			}
			
			BitArray expectedSet;
			
			try {
				expectedSet = p.GetExpectedSet();
			} catch (InvalidOperationException) {
				expectedSet = null;
			}
			
			if (p.NextTokenIsPotentialStartOfExpression)
				return new ExpressionResult("", new DomRegion(targetPosition.Line, targetPosition.Column), context, expectedSet);
			
			int lastExpressionStartOffset = LocationToOffset(block.lastExpressionStart);
			
			if (lastExpressionStartOffset < 0)
				return new ExpressionResult("", new DomRegion(targetPosition.Line, targetPosition.Column), context, expectedSet);
			
			return MakeResult(text, lastExpressionStartOffset, offset, context, expectedSet);
		}

		ExpressionResult MakeResult(string text, int startOffset, int endOffset, ExpressionContext context, BitArray expectedKeywords)
		{
			return new ExpressionResult(TrimComment(text.Substring(startOffset, endOffset - startOffset)).Trim(),
			                            DomRegion.FromLocation(OffsetToLocation(startOffset), OffsetToLocation(endOffset)),
			                            context, expectedKeywords);
		}
		
		string TrimComment(string text)
		{
			bool inString = false;
			int i = 0;
			
			while (i < text.Length) {
				char ch = text[i];
				
				if (ch == '"')
					inString = !inString;
				if (ch == '\'' && !inString) {
					int eol = text.IndexOfAny(new[] { '\r', '\n' }, i);
					
					if (eol > -1) {
						if(text[eol] == '\r' && eol + 1 < text.Length && text[eol + 1] == '\n')
							eol++;
						
						text = text.Remove(i, eol - i);
					} else {
						text = text.Remove(i);
					}
					
					continue;
				}
				
				i++;
			}
			
			return text;
		}
		
		void Init(string text, int offset)
		{
			lineOffsets = new List<int>();
			lineOffsets.Add(0);
			for (int i = 0; i < text.Length; i++) {
				if (i == offset) {
					targetPosition = new Location(offset - lineOffsets[lineOffsets.Count - 1] + 1, lineOffsets.Count);
				}
				if (text[i] == '\n') {
					lineOffsets.Add(i + 1);
				} else if (text[i] == '\r') {
					if (i + 1 < text.Length && text[i + 1] != '\n') {
						lineOffsets.Add(i + 1);
					}
				}
			}
			if (offset == text.Length) {
				targetPosition = new Location(offset - lineOffsets[lineOffsets.Count - 1] + 1, lineOffsets.Count);
			}
		}
		
		ExpressionContext GetContext(Block block)
		{
			switch (block.context) {
				case Context.Global:
					return ExpressionContext.Global;
				case Context.TypeDeclaration:
					return ExpressionContext.TypeDeclaration;
				case Context.Type:
					return ExpressionContext.Type;
				case Context.Body:
					return ExpressionContext.MethodBody;
				case Context.Importable:
					return ExpressionContext.Importable;
				case Context.ObjectCreation:
					return ExpressionContext.ObjectCreation;
				case Context.Parameter:
					return ExpressionContext.Parameter;
			}
			
			return ExpressionContext.Default;
		}
		
		public ExpressionResult FindFullExpression(string text, int offset)
		{
			Init(text, offset);
			
			ExpressionFinder p = new ExpressionFinder();
			lexer = ParserFactory.CreateLexer(SupportedLanguage.VBNet, new StringReader(text));
			Token t;
			
			Block block = null;
			
			var expressionDelimiters = new[] { Tokens.EOL, Tokens.Colon, Tokens.Dot, Tokens.TripleDot, Tokens.DotAt };
			
			while (true) {
				t = lexer.NextToken();
				p.InformToken(t);
				
				if (block == null && t.EndLocation > targetPosition)
					block = p.CurrentBlock;
				if (block != null && (block.isClosed || expressionDelimiters.Contains(t.Kind) && block == p.CurrentBlock))
					break;
			}
			
			if (p.Errors.Any()) {
				foreach (var e in p.Errors)
					LoggingService.Warn("not expected: " + e);
			}
			
			BitArray expectedSet;
			
			try {
				expectedSet = p.GetExpectedSet();
			} catch (InvalidOperationException) {
				expectedSet = null;
			}
			
			int tokenOffset;
			if (t == null || t.Kind == Tokens.EOF)
				tokenOffset = text.Length;
			else
				tokenOffset = LocationToOffset(t.Location);

			int lastExpressionStartOffset = LocationToOffset(block.lastExpressionStart);
			if (lastExpressionStartOffset >= 0) {
				if (offset < tokenOffset) {
					// offset is in front of this token
					return MakeResult(text, lastExpressionStartOffset, tokenOffset, GetContext(block), expectedSet);
				} else {
					// offset is IN this token
					return MakeResult(text, lastExpressionStartOffset, offset, GetContext(block), expectedSet);
				}
			} else {
				return new ExpressionResult(null, GetContext(block));
			}
		}
		
		public string RemoveLastPart(string expression)
		{
			return expression;
		}
		
		#region Helpers
		
		#endregion
	}
}