#! /bin/bash

if [ -z "$1" ]; then
    echo "No input directory provided"
    exit 1
else
    rootDirectory=$1
fi

if [ -z "$2" ]; then
    echo "No output file provided"
    exit 1
else
    outputFile=$2
fi

echo $rootDirectory
IFS=$'\n' files=($(find "${rootDirectory}" -type f -name '*.so' -or -name '*.dylib' -or -name 'mono-sgen' | grep -v dSYM))

for ((i = 0; i < ${#files[@]}; i++))
do
    filename="${files[$i]}"
    echo "Name: $filename" >> $outputFile
    echo "Name: $filename"
    nm -n "$filename" | grep ' t ' | cut -f 1,3 -d ' ' >> $outputFile
done