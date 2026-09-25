from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .facet_value import FacetValue

@dataclass
class SearchFacets(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The container property
    container: Optional[list[FacetValue]] = None
    # The contentType property
    content_type: Optional[list[FacetValue]] = None
    # The term property
    term: Optional[list[FacetValue]] = None
    # The workspace property
    workspace: Optional[list[FacetValue]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SearchFacets:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SearchFacets
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SearchFacets()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .facet_value import FacetValue

        from .facet_value import FacetValue

        fields: dict[str, Callable[[Any], None]] = {
            "container": lambda n : setattr(self, 'container', n.get_collection_of_object_values(FacetValue)),
            "contentType": lambda n : setattr(self, 'content_type', n.get_collection_of_object_values(FacetValue)),
            "term": lambda n : setattr(self, 'term', n.get_collection_of_object_values(FacetValue)),
            "workspace": lambda n : setattr(self, 'workspace', n.get_collection_of_object_values(FacetValue)),
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
        writer.write_collection_of_object_values("container", self.container)
        writer.write_collection_of_object_values("contentType", self.content_type)
        writer.write_collection_of_object_values("term", self.term)
        writer.write_collection_of_object_values("workspace", self.workspace)
        writer.write_additional_data_value(self.additional_data)
    

