from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .linked_item import LinkedItem

@dataclass
class TaskLinksResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The blockedBy property
    blocked_by: Optional[list[LinkedItem]] = None
    # The blocking property
    blocking: Optional[list[LinkedItem]] = None
    # The documents property
    documents: Optional[list[LinkedItem]] = None
    # The parent property
    parent: Optional[LinkedItem] = None
    # The subtasks property
    subtasks: Optional[list[LinkedItem]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> TaskLinksResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: TaskLinksResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return TaskLinksResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .linked_item import LinkedItem

        from .linked_item import LinkedItem

        fields: dict[str, Callable[[Any], None]] = {
            "blockedBy": lambda n : setattr(self, 'blocked_by', n.get_collection_of_object_values(LinkedItem)),
            "blocking": lambda n : setattr(self, 'blocking', n.get_collection_of_object_values(LinkedItem)),
            "documents": lambda n : setattr(self, 'documents', n.get_collection_of_object_values(LinkedItem)),
            "parent": lambda n : setattr(self, 'parent', n.get_object_value(LinkedItem)),
            "subtasks": lambda n : setattr(self, 'subtasks', n.get_collection_of_object_values(LinkedItem)),
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
        writer.write_collection_of_object_values("blockedBy", self.blocked_by)
        writer.write_collection_of_object_values("blocking", self.blocking)
        writer.write_collection_of_object_values("documents", self.documents)
        writer.write_object_value("parent", self.parent)
        writer.write_collection_of_object_values("subtasks", self.subtasks)
        writer.write_additional_data_value(self.additional_data)
    

