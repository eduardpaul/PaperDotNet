from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .document_response import DocumentResponse
    from .file_version_response import FileVersionResponse

@dataclass
class PageOperationResponse(AdditionalDataHolder, Parsable):
    """
    The result: the source's new version (null when it was deleted), new documents, the target's new version.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The documents property
    documents: Optional[list[DocumentResponse]] = None
    # The source property
    source: Optional[FileVersionResponse] = None
    # The sourceDeleted property
    source_deleted: Optional[bool] = None
    # The target property
    target: Optional[FileVersionResponse] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> PageOperationResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: PageOperationResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return PageOperationResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .document_response import DocumentResponse
        from .file_version_response import FileVersionResponse

        from .document_response import DocumentResponse
        from .file_version_response import FileVersionResponse

        fields: dict[str, Callable[[Any], None]] = {
            "documents": lambda n : setattr(self, 'documents', n.get_collection_of_object_values(DocumentResponse)),
            "source": lambda n : setattr(self, 'source', n.get_object_value(FileVersionResponse)),
            "sourceDeleted": lambda n : setattr(self, 'source_deleted', n.get_bool_value()),
            "target": lambda n : setattr(self, 'target', n.get_object_value(FileVersionResponse)),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_object_values("documents", self.documents)
        writer.write_object_value("source", self.source)
        writer.write_bool_value("sourceDeleted", self.source_deleted)
        writer.write_object_value("target", self.target)
        writer.write_additional_data_value(self.additional_data)
    

