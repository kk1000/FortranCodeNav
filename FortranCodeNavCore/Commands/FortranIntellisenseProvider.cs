﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FortranCodeNavCore.Fortran;
using FortranCodeNavCore.Fortran.Elements;
using FortranCodeNavCore.Fortran.Parser;
using FortranCodeNavCore.SyntaxTrees;
using VSIntegration;
using VSIntegration.CodeComplete;
using Type = FortranCodeNavCore.Fortran.Elements.Type;
using System.Collections;
using System.Globalization;
using System.Drawing;

namespace FortranCodeNavCore.Commands
{
    public class FortranIntellisenseProvider : FortranCommandBase
    {
        private readonly Regex callRegex = new Regex(@"call\s+(\w*)$", RegexOptions.IgnoreCase);

        private readonly string[] fortranKeywords = new[]
                                                        {
                                                            "IF", "ENDIF", "THEN", "CALL", "RETURN",
                                                            "END", "IMPLICIT ", "PROGRAM", "MODULE", "FUNCTION",
                                                            "SUBROUTINE", "TYPE", "REAL", "INTEGER",
                                                            "DOUBLE PRECISION", "CHARACTER", "LOGICAL",
                                                            "DO", "ENDDO", "READ", "READ(*,*)", "WRITE", "WRITE(*,*)",
                                                            "FORMAT", "END SUBROUTINE", "END FUNCTION",
                                                            "END MODULE", "END PROGRAM", "CONTINUE", "GOTO"
                                                        };

        // Initialize the fortran intrinsics (+ tooltip) from the Resources.resx file.
        private readonly Dictionary<string, string> fortranIntrinsics = Properties.Resources.ResourceManager
            .GetResourceSet(CultureInfo.CurrentUICulture, true, true)
            .Cast<DictionaryEntry>()
            .Where(de => ((string)de.Key).StartsWith("intr_"))
            .ToDictionary(de => ((string)de.Key).Substring(5).ToUpper(), de => (string)de.Value);

        private static readonly Bitmap IntrinsicIcon = Properties.Resources.Intrinsic_Icon;

        internal void OnCodeCompleteActivating(CompletionSession session)
        {
            session.Coordinate = VisualStudio.GetCaretPositionInScreenCoordinates();
            UpdateCodeComplete(session);
        }

        internal void OnCodeCompleteUpdating(CompletionSession session)
        {
            UpdateCodeComplete(session);
        }
        
        /// <summary>
        /// This method parses the current line, and determines which suggestions to show for code completion (intellisense)
        /// </summary>
        /// <param name="session"></param>
        private void UpdateCodeComplete(CompletionSession session)
        {
            //parse current line
            //set results
            //set filter
            try
            {
                ParseQueueProcessor.ShowProgressDialog = false; //maybe not actually required anymore  

                var currentLine = VisualStudio.GetCurrentLine();

                var currentIndex = VisualStudio.GetCursorPositionInLine();

                var lineParser = new FortranStatementParser();

                var beginOfStatement = 0;

                var statement = lineParser.FindEffectiveStatementAtOffset(currentLine, currentIndex, out beginOfStatement);
                var fullStatement = currentLine.Substring(beginOfStatement, currentIndex - beginOfStatement);

                var currentMember = GetCurrent<IMember>();

                ShowMethodSignatureAsToolTip(session, currentMember, currentLine, currentIndex);

                IList<INameable> codeCompleteOptions;
                var filter = "";
                var addLanguageKeywords = false;
                var codeElementsInScope =
                    FortranSyntaxTreeModel.GetElementsAvailableInScope(SyntaxTreeMaintainer.GetSyntaxTrees(),
                                                                      currentMember);

                // are we in a 'call something' statement?
                var callMatch = callRegex.Match(statement);
                if (callMatch.Success)
                {
                    //only subroutines
                    codeCompleteOptions = codeElementsInScope.OfType<Subroutine>().Cast<INameable>().ToList(); //subroutines only
                    session.InsertionIndexInLine = beginOfStatement + callMatch.Groups[1].Index;
                    filter = callMatch.Groups[1].Value;
                }
                else if (!statement.Contains('%')) //local scope
                {
                    if (statement.Length > 0 &&
                        FortranParseHelper.IsWhiteSpace(statement[statement.Length - 1]))
                    {
                        //reset: wrong statement:
                        beginOfStatement += statement.Length;
                        statement = "";
                    }

                    codeCompleteOptions = codeElementsInScope.Where(ce => !(ce is Subroutine)).ToList(); //subroutines only available after a 'call'
                    session.InsertionIndexInLine = beginOfStatement;
                    filter = statement;
                    addLanguageKeywords = true;
                }
                else // nested in type, eg: channel % sourceNode % id
                {
                    var elements = FortranParseHelper.SplitElementsInStatement(statement, fullStatement, ref beginOfStatement, out filter);

                    if (elements.Count < 1)
                    {
                        throw new NotSupportedException("help!");
                    }

                    var variableName = elements[0];

                    var matchingVariable = codeElementsInScope
                        .OfType<Variable>()
                        .FirstOrDefault(lv => String.Equals(lv.Name, variableName, StringComparison.InvariantCultureIgnoreCase));

                    var typeOfVariable = matchingVariable.TypeString;

                    var currentType = codeElementsInScope
                        .OfType<Type>()
                        .FirstOrDefault(tp => String.Equals(tp.Name, typeOfVariable, StringComparison.InvariantCultureIgnoreCase));

                    for (int i = 1; i < elements.Count; i++)
                    {
                        if (currentType == null)
                            break;

                        var propertyName = elements[i];
                        var property = GetElementsOfType(currentType).FirstOrDefault(vf => String.Equals(vf.Name, propertyName, StringComparison.InvariantCultureIgnoreCase));

                        typeOfVariable = property.TypeString;

                        currentType = codeElementsInScope
                            .OfType<Type>()
                            .FirstOrDefault(tp => String.Equals(tp.Name, typeOfVariable, StringComparison.InvariantCultureIgnoreCase));
                    }

                    if (currentType == null)
                        codeCompleteOptions = new INameable[] { };
                    else
                        codeCompleteOptions = GetElementsOfType(currentType).Cast<INameable>().ToList();

                    session.InsertionIndexInLine = beginOfStatement;
                }

                // gather all results
                var completionItems = codeCompleteOptions.Select(e => new CompletionItem(e.Name, FortranIconProvider.GetIconForMember(e)) { ToolTip = GetTooltipForMember(currentMember, e) }).ToList();
                if (addLanguageKeywords)
                {
                    foreach (var keyword in fortranKeywords)
                    {
                        completionItems.Add(new CompletionItem(keyword, null));
                    }

                    foreach (var intrinsic in fortranIntrinsics)
                    {
                        completionItems.Add(new CompletionItem(intrinsic.Key, IntrinsicIcon) { ToolTip = intrinsic.Value });
                    }
                }

                {//YK, 2020, also add all the words already in this current file
                    //check file content.
                    var myFileContents = VisualStudio.GetCurrentCodeFileContent();

                    {
                        ////two methods to replace \t, \n, \r, tested and works
                        //myFileContents = Regex.Replace(myFileContents, @"\t", " ");
                        ////myFileContents = myFileContents.Replace("\t", " ");
                        //myFileContents = myFileContents.Replace("\r", " ");
                        //myFileContents = myFileContents.Replace("\n", " ");
                    }

                    {//example to remove separator...
                        //char[] separators = new char[] { ' ', ';', ',', '\r', '\t', '\n', '(', ')', '!', ':', '=', '+', '*', '/' };
                        //string s = "replace;multiple,characters\tin;a,c\rsharp,string";
                        //string[] temp = s.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                        //s = String.Join("\n", temp);
                    }

                    {//example to remove duplicated string
                        //String[] a = { "1", "1", "2", "1", "1", "3", "1", "1", };
                        //Console.WriteLine("原数组长度:    {0}", a.Length);
                        //Console.WriteLine("排除后数组长度:{0}", a.Distinct<string>().ToArray().Length);
                        //Console.ReadKey();
                    }

                    char[] separators = new char[] { ' ', ';', ',', '\r', '\t', '\n', '(', ')', '!',  ':', '=', '+', '-', '*', '/', '&', '.', '\'', '<', '>', '{', '}', '[', ']', '$', '%', '?'};
                    string s = myFileContents;                    
                    string[] myFileContentSplittedStr = myFileContents.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                    string[] myFileContentSplittedStrDistinct = myFileContentSplittedStr.Distinct<string>().ToArray();
                    foreach (var keyword in myFileContentSplittedStrDistinct)
                    {
                        //{//example to add non-duplicated string
                        //    //if (!listString.Contains(eachString))
                        //    //    listString.Add(eachString);
                        //}

                        completionItems.Add(new CompletionItem(keyword, null));
                    }



                }

                session.SetCompletionSet(completionItems, filter);
            }
            catch (Exception e)
            {
                Log.Error("Error while updating code complete", e);
            }
            finally
            {
                ParseQueueProcessor.ShowProgressDialog = true; //messes with intellisense popup
            }
        }

        private void ShowMethodSignatureAsToolTip(CompletionSession session, IMember currentMember, string currentLine, int currentIndex)
        {
            session.SignatureToolTip = "";

            try
            {
                if (currentMember == null)
                    return;

                var lineParser = new FortranStatementParser();
                var methodName = lineParser.FindMethodAtOffset(currentLine, currentIndex);

                if (String.IsNullOrEmpty(methodName)) 
                    return;

                var allMethodsInScope = FortranSyntaxTreeModel.GetElementsAvailableInScope(SyntaxTreeMaintainer.GetSyntaxTrees(), currentMember).
                                                               OfType<IMethod>();

                var method = allMethodsInScope.FirstOrDefault(m => String.Equals(m.Name, methodName,
                                                                                 StringComparison.InvariantCultureIgnoreCase));

                if (method == null)
                {
                    if (fortranIntrinsics.ContainsKey(methodName))
                    {
                        var intrinsicDoc = fortranIntrinsics[methodName];
                        var fistLineOfDoc = intrinsicDoc.Split('\n').FirstOrDefault() ?? ""; // take first line of documentation
                        session.SignatureToolTip = fistLineOfDoc;
                    }
                    return;
                }

                var declarationParser = new FortranDeclarationParser();
                var signatureParser = new FortranSignatureParser();
                declarationParser.ParseDeclarationsAndUses(method);
                signatureParser.ParseMethodSignature(method);

                session.SignatureToolTip = method.Name +
                                           "(" +
                                           String.Join(", ", method.Parameters.Select(p => p.TypeString + " " + p.Name).ToArray()) +
                                           ")";
            }
            catch (Exception e)
            {
                Log.Error("Exception while generating signature tooltip", e);
            }
        }

        private static string GetTooltipForMember(IMember currentMember, INameable element)
        {
            var type = "(unknown type)";
            var scope = "(unknown scope)";
            try
            {
                if (element is IMember)
                {
                    var member = element as IMember;
                    scope = member.Parent == currentMember ? "(local)" : member.GetScopeDescription();
                    type = element.GetType().Name;
                }
                else if (element is Variable)
                {
                    var variable = element as Variable;
                    type = variable.TypeString;
                    scope = variable.Member.GetType().Name + " " + variable.Member.Name;
                }
            }
            catch (Exception e)
            {
                Log.Error("Error getting type/scope for element " + element.Name + " while generating tooltip", e);
            }

            return type + ", in " + scope;
        }

        private static IEnumerable<Variable> GetElementsOfType(Type currentType)
        {
            new FortranDeclarationParser().ParseDeclarationsAndUses(currentType);
            return currentType.LocalVariables;
        }
        
        public FortranIntellisenseProvider(VisualStudioIDE visualStudio, SyntaxTreeMaintainer syntaxTreeMaintainer)
            : base(visualStudio, syntaxTreeMaintainer)
        {
        }
    }
}